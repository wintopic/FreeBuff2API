#!/usr/bin/env python3
"""Freebuff 一键获取 authToken 脚本（授权码轮询流程，交互方式对齐 cline_oauth.py）。

用法：
  python3 extract_freebuff.py login           # 开始登录（授权链接推 TG + 轮询拿 token）
  python3 extract_freebuff.py tgsend          # 测试 TG 连通性（发一条测试消息）
  python3 extract_freebuff.py show            # 显示全部账号（邮箱+完整token+存活状态+汇总一行一个）
  python3 extract_freebuff.py session         # 测试开 session（POST）
  python3 extract_freebuff.py chat [消息]     # 发一条消息测试模型 API
  python3 extract_freebuff.py quota           # 查用量 /api/v1/usage
  python3 extract_freebuff.py export          # 汇总全部账号 token 一行一个（复制进 CF Workers 变量）

流程（与官方 CLI 一致）：
  1. 生成设备指纹 fingerprintId
  2. POST https://www.codebuff.com/api/auth/cli/code → 拿 Google 登录 URL + fingerprintHash
  3. 授权链接打印 + 推送 TG，用户在浏览器打开并登录（脚本自动轮询）
  4. 轮询 /api/auth/cli/status → 成功拿到 user（含 authToken）
  5. authToken 保存到本地 / 推送 TG，之后直接作为 Bearer 调模型 API

GitHub Actions 里的安全行为（重要）：
  * 配置了 TG_BOT_TOKEN / TG_CHAT_ID 时，授权链接与 authToken 一律推送到 Telegram，
    **authToken 绝不打印到标准输出/日志**（即使误打印也会被 ::add-mask:: 打码）。
  * 未配置 TG 时（本地手动跑），保持原样打印，方便直接查看。
  * TG 推送失败时直接报错退出，绝不把 token 落到日志里。

环境变量：
  TG_BOT_TOKEN         Telegram Bot Token（可选；与 TG_CHAT_ID 一起配置才推送）
  TG_CHAT_ID           Telegram 接收 chat_id（可选）
  FREEBUFF_TOKEN       手动指定 authToken（跳过 credentials 文件）

依赖：仅 Python 3 标准库，无需 pip 安装任何东西。
"""
import argparse
import base64
import json
import os
import secrets
import sys
import time
import urllib.parse
import urllib.request
from pathlib import Path

BASE_URL = "https://www.codebuff.com"
CRED_FILE = Path(__file__).resolve().parent / "freebuff_credentials.json"
POLL_INTERVAL = 5          # 秒，官方 CLI 用 5s
POLL_TIMEOUT = 5 * 60      # 秒，官方 5 分钟
REQUEST_TIMEOUT = 30
SDK_UA = "ai-sdk/openai-compatible/1.0.25/codebuff"

MODEL_DEFAULT = "deepseek/deepseek-v4-flash"


# ---------------------------------------------------------------------------
# CI / Telegram helpers（对齐 cline_oauth.py 的交互方式）
# ---------------------------------------------------------------------------

def in_ci():
    return os.environ.get("GITHUB_ACTIONS") == "true"


def tg_configured():
    return bool(os.environ.get("TG_BOT_TOKEN") and os.environ.get("TG_CHAT_ID"))


def send_tg(text):
    """推送文本到 Telegram，失败返回 False（错误描述打印到 stderr，便于定位）。"""
    token = os.environ.get("TG_BOT_TOKEN")
    chat = os.environ.get("TG_CHAT_ID")
    if not token or not chat:
        return False
    url = f"https://api.telegram.org/bot{token}/sendMessage"
    body = json.dumps({"chat_id": chat, "text": text}).encode()
    req = urllib.request.Request(url, data=body, method="POST",
                                 headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=15) as r:
            data = json.loads(r.read().decode() or "{}")
            if not data.get("ok", True):
                print(f"   ⚠️ TG API 错误: {data.get('description', data)}")
                return False
            return True
    except urllib.error.HTTPError as e:
        try:
            err = json.loads(e.read().decode() or "{}")
            desc = err.get("description", str(e))
        except Exception:
            desc = str(e)
        print(f"   ⚠️ TG 发送失败: {desc}")
        return False
    except Exception as e:
        print(f"   ⚠️ TG 发送失败: {e}")
        return False


def mask_value(value):
    """在 CI 中把敏感值加入 GitHub Actions 日志掩码（即使误打出也被打码）。"""
    if in_ci() and value:
        print(f"::add-mask::{value}")


# ---------------------------------------------------------------------------
# HTTP helpers（标准库 urllib，无第三方依赖）
# ---------------------------------------------------------------------------

def _http(method: str, path: str, body=None, headers=None, query=None, timeout=REQUEST_TIMEOUT):
    url = BASE_URL + path
    if query:
        url += "?" + urllib.parse.urlencode(query)
    data = None
    hdrs = {
        "User-Agent": SDK_UA,
        "Accept": "application/json",
    }
    if body is not None:
        data = json.dumps(body).encode()
        hdrs["Content-Type"] = "application/json"
    if headers:
        hdrs.update(headers)
    req = urllib.request.Request(url, data=data, headers=hdrs, method=method)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            raw = resp.read()
            return resp.status, json.loads(raw) if raw else None, resp.headers
    except urllib.error.HTTPError as e:
        raw = e.read()
        try:
            parsed = json.loads(raw) if raw else None
        except Exception:
            parsed = raw.decode(errors="replace")[:500]
        return e.code, parsed, e.headers
    except Exception as e:
        return None, {"error": str(e)}, None


def get_token():
    tok = os.environ.get("FREEBUFF_TOKEN")
    if tok:
        return tok
    if CRED_FILE.exists():
        cred = json.loads(CRED_FILE.read_text())
        # 兼容旧格式 {"default": {...}}
        tok = cred.get("authToken")
        if not tok:
            tok = cred.get("default", {}).get("authToken")
        if not tok:
            # 新格式 {"accounts": {"<key>": {...}}}：取第一个账号
            accts = cred.get("accounts") or {}
            for u in accts.values():
                tok = u.get("authToken")
                if tok:
                    break
        return tok
    return None


def _account_key(user: dict) -> str:
    """账号唯一键：优先 id，其次 email，最后 authToken 前缀。"""
    uid = user.get("id") or ""
    email = user.get("email") or ""
    if uid:
        return str(uid)
    if email:
        return str(email)
    tok = user.get("authToken") or ""
    return f"token-{tok[:12]}" if tok else "unknown"


def save_credentials(user: dict, append: bool = True):
    """保存凭证。append=True 时按账号分键追加（不覆盖其他账号）；
    append=False 时写为 default（兼容旧格式，CI 用）。"""
    existing = {}
    if CRED_FILE.exists():
        try:
            existing = json.loads(CRED_FILE.read_text())
        except Exception:
            pass
    if append:
        # 新格式：accounts 分键，保留已有账号
        accts = existing.get("accounts")
        if not isinstance(accts, dict):
            accts = {}
            # 迁移旧格式 default → accounts
            if isinstance(existing.get("default"), dict):
                accts[_account_key(existing["default"])] = existing["default"]
            existing = {"accounts": accts}
        key = _account_key(user)
        accts[key] = user
        existing["accounts"] = accts
    else:
        # 旧格式：直接覆盖 default（CI 单账号场景）
        existing["default"] = user
    CRED_FILE.write_text(json.dumps(existing, indent=2, ensure_ascii=False))
    accts = existing.get("accounts")
    acct_count = len(accts) if isinstance(accts, dict) else (1 if existing.get("default") else 0)
    print(f"💾 凭证已保存 → {CRED_FILE}（当前 {acct_count} 个账号）")


# ---------------------------------------------------------------------------
# 各功能
# ---------------------------------------------------------------------------

def gen_fingerprint():
    """官方 legacy fallback 格式：codebuff-cli-<8位随机>"""
    rand = base64.urlsafe_b64encode(secrets.token_bytes(6)).decode().rstrip("=")[:8]
    return f"codebuff-cli-{rand}"


def cmd_tgsend(args):
    """测试 TG 连通性：发一条测试消息。"""
    if not tg_configured():
        print("❌ 未设置 TG_BOT_TOKEN / TG_CHAT_ID")
        sys.exit(1)
    ok = send_tg("✅ TG 连通性测试成功！\nFreebuff 提取工作流可以正常向你发消息。")
    if ok:
        print("✅ 测试消息已发送到 TG，请查收。")
    else:
        print("❌ TG 发送失败，请检查 TG_BOT_TOKEN / TG_CHAT_ID。")
        sys.exit(1)


def cmd_login(args):
    # 交互方式：TG 配置了就推 TG；CI 环境强制要求 TG（workflow 第一步也会拦）
    if in_ci() and not tg_configured():
        print("::error::Actions 环境强制 TG 模式，请先配置 TG_BOT_TOKEN 和 TG_CHAT_ID")
        sys.exit(1)
    use_tg = tg_configured()

    fingerprint_id = args.fingerprint or gen_fingerprint()
    print(f"🚀 启动 Freebuff 登录流程（fingerprintId: {fingerprint_id}）...\n")

    status, data, _ = _http("POST", "/api/auth/cli/code", {"fingerprintId": fingerprint_id})
    if status != 200 or not data:
        msg = f"❌ 请求登录 URL 失败: HTTP {status} {data}"
        print(msg)
        if use_tg:
            send_tg("⚠️ Freebuff 提取失败：\n" + msg)
        sys.exit(1)

    login_url = data["loginUrl"]
    fingerprint_hash = data["fingerprintHash"]
    expires_at = data["expiresAt"]
    # loginUrl 含一次性 auth_code，CI 里掩码，避免暴露到日志
    mask_value(login_url)

    # 可选的轮询超时覆盖（对齐 cline_oauth.py：workflow 的 poll_timeout 传进来）
    poll_timeout = POLL_TIMEOUT
    env_timeout = os.environ.get("OAUTH_POLL_TIMEOUT")
    if env_timeout:
        try:
            poll_timeout = int(env_timeout)
        except ValueError:
            pass

    # 把授权链接推送到 TG，方便在手机上完成授权
    if use_tg:
        tg_msg = (
            "🔑 *Freebuff 授权请求*\n\n"
            "请在浏览器打开下面链接并完成登录：\n"
            f"{login_url}\n\n"
            f"脚本将自动轮询等待，最多 {poll_timeout} 秒。"
        )
        ok = send_tg(tg_msg)
        if not ok:
            print("❌ 授权链接推送 TG 失败（请检查 TG_BOT_TOKEN / TG_CHAT_ID）")
            sys.exit(1)
        print("📨 授权链接已推送到 Telegram（URL 不打印到日志）。")
    else:
        # 非 TG（本地手动跑）才打印 URL
        print("=" * 60)
        print("1️⃣  在浏览器打开下面这个链接：")
        print(f"    {login_url}")
        print("2️⃣  用 Google 账号登录并授权")
        print(f"3️⃣  脚本自动轮询等待，最多 {poll_timeout} 秒")
        print("=" * 60)

    print(f"\n🔄 等待你授权（脚本自动轮询，最多 {poll_timeout} 秒）...")
    start = time.time()
    attempts = 0
    while time.time() - start < poll_timeout:
        attempts += 1
        status, data, _ = _http(
            "GET", "/api/auth/cli/status",
            query={
                "fingerprintId": fingerprint_id,
                "fingerprintHash": fingerprint_hash,
                "expiresAt": expires_at,
            },
        )
        if status == 200 and data and data.get("user"):
            user = data["user"]
            if not user.get("authToken"):
                print(f"⚠️ 返回 user 但没有 authToken: {json.dumps(user)[:300]}")
                sys.exit(1)
            print(f"✅ 登录成功！（第 {attempts} 次轮询，{int(time.time()-start)}s）")

            email = user.get("email", "unknown")
            # 邮箱 / id 同样视为敏感信息：打码，避免进入 Actions 日志
            mask_value(email)
            mask_value(str(user.get("id", "")))
            print(f"✅ 登录成功! 账号: {email}")

            # 本地运行：分账号追加（不覆盖已有账号）；CI 运行：覆盖 default
            save_credentials(user, append=not in_ci())

            # 关键安全点：CI + 配置了 TG 时，authToken 只推 TG，绝不打印到日志
            auth_token = user["authToken"]
            if use_tg:
                mask_value(auth_token)  # 兜底：即使万一打出也会被 Actions 掩码
                ok = send_tg(
                    "🔑 *Freebuff authToken 已获取*\n\n"
                    f"账号：`{email}`\n"
                    f"id：`{user.get('id')}`\n"
                    f"credits：`{user.get('credits')}`\n\n"
                    "把下面这行填进 Cloudflare Worker 机密变量 `FREEBUFF_TOKEN`"
                    "（多账号则换行追加）：\n"
                    f"`{auth_token}`"
                )
                if not ok:
                    print("❌ authToken 推送 TG 失败！token 未打印到日志，请检查 TG 配置后重试。")
                    sys.exit(1)
                print("🔑 authToken 已通过 Telegram 私密发送（未写入日志）。")
            else:
                mask_value(auth_token)
                print("\n🔑 把下面这行填进 Cloudflare Worker 的机密变量 FREEBUFF_TOKEN：")
                print("    " + auth_token)
            return user
        elif status == 401:
            print(f"   [{int(time.time()-start)}s] 尚未登录（401），继续等待…")
        elif status == 400:
            print(f"❌ 登录请求已失效: {data}")
            sys.exit(1)
        else:
            print(f"   [{int(time.time()-start)}s] 状态 {status}: {str(data)[:120]}")
        time.sleep(POLL_INTERVAL)

    print("⏰ 等待登录超时，请重试。")
    sys.exit(1)


def cmd_show(_args):
    """显示全部账号：邮箱 + token（完整显示，本地工具无需脱敏）+ 存活状态（0 消耗 GET /session），末尾汇总一行一个。"""
    pairs = _all_tokens()
    if not pairs:
        print("❌ 未找到 authToken（先运行 login 或设置 FREEBUFF_TOKEN）")
        sys.exit(1)
    print(f"📋 已保存凭证（{len(pairs)} 个账号）:")
    print("-" * 60)
    for _key, at, email in pairs:
        verdict, detail = _check_one(at)
        print(f"  [{email}] {verdict}")
        print(f"      {at}")
        print(f"      {detail}")
    print("-" * 60)
    print("\n📋 汇总（一行一个，复制进 CF Worker 变量 FREEBUFF_TOKEN）:")
    for _key, at, _email in pairs:
        print(f"   {at}")
    return 0


def cmd_session(args):
    tok = get_token()
    if not tok:
        print("❌ 未找到 authToken")
        sys.exit(1)
    headers = {"Authorization": f"Bearer {tok}"}
    model = args.model or MODEL_DEFAULT
    if args.post:
        headers["x-freebuff-model"] = model
        status, data, _ = _http("POST", "/api/v1/freebuff/session", headers=headers)
    else:
        status, data, _ = _http("GET", "/api/v1/freebuff/session", headers=headers)
    print(f"📡 HTTP {status}")
    print(json.dumps(data, indent=2, ensure_ascii=False) if data else "(空响应)")
    return data


# 官方 free-mode marker：系统提示必须以 canonical Buffy 开头（字节级 position 0）
# 旧 `[System Override...]` 前缀绕过已被官方修补（403 free_mode_cli_required）
CANONICAL_BUFFY = "You are Buffy, the strategic coding assistant."

# 模型 → 上游 agentId（对齐 worker.js 的 MODELS 表；free 模式校验 agent+model 组合）
MODEL_AGENTS = {
    "deepseek/deepseek-v4-flash": "base2-free-deepseek-flash",
    "deepseek/deepseek-v4-pro": "base2-free-deepseek",
    "moonshotai/kimi-k2.6": "base2-free-kimi",
    "minimax/minimax-m2.7": "base2-free",
    "minimax/minimax-m3": "base2-free-minimax-m3",
    "mimo/mimo-v2.5": "base2-free-mimo",
    "mimo/mimo-v2.5-pro": "base2-free-mimo-pro",
}


def agent_for_model(model):
    return MODEL_AGENTS.get(model, "base2-free-deepseek-flash")


def cmd_chat(args):
    tok = get_token()
    if not tok:
        print("❌ 未找到 authToken")
        sys.exit(1)

    # 1) 先确保有 active session（官方门控：无 session → 428 waiting_room_required）
    model = args.model or MODEL_DEFAULT
    # 官方 SDK UA（free 模式识别依赖，浏览器 UA 会被拒）
    headers = {"Authorization": f"Bearer {tok}", "User-Agent": SDK_UA}
    status, sess, _ = _http("POST", "/api/v1/freebuff/session",
                            headers={**headers, "x-freebuff-model": model})
    print(f"📡 POST /session → HTTP {status}")
    instance_id = None
    if isinstance(sess, dict) and sess.get("status") == "active":
        instance_id = sess.get("instanceId")
        print(f"   ✅ session active, instanceId={instance_id}, "
              f"model={sess.get('model')}, expires_at={sess.get('expires_at')}")
    else:
        print(f"   ⚠️ {str(sess)[:300]}")
        if not args.force:
            print("   （使用 --force 仍尝试直发 chat 看报错）")
            sys.exit(1)

    # 1.5) 先 START 一个 run，拿真实 runId（chat 校验 run_id 存在；agent 按模型映射）
    run_id = args.run_id
    agent_id = args.agent or agent_for_model(model)
    if not run_id:
        s, sr, _ = _http("POST", "/api/v1/agent-runs",
                         {"action": "START", "agentId": agent_id,
                          "ancestorRunIds": []}, headers)
        if isinstance(sr, dict) and sr.get("runId"):
            run_id = sr["runId"]
            print(f"   📡 START run → HTTP {s} runId={run_id} (agent={agent_id})")
        else:
            print(f"   ⚠️ START run 失败 HTTP {s}: {str(sr)[:200]}")
            if not args.force:
                sys.exit(1)

    # 2) 调 chat/completions：canonical Buffy 开头 + SDK UA + acting-user-id + data_collection deny
    chat_headers = {
        "Authorization": f"Bearer {tok}",
        "Content-Type": "application/json",
        "User-Agent": SDK_UA,
    }
    if instance_id:
        chat_headers["x-freebuff-instance-id"] = instance_id
    # 有凭证 id 就带 acting-user-id（官方 SDK 会带）
    uid = None
    if CRED_FILE.exists():
        try:
            uid = json.loads(CRED_FILE.read_text()).get("default", {}).get("id")
        except Exception:
            pass
    if uid:
        chat_headers["x-freebuff-acting-user-id"] = uid

    body = {
        "model": model,
        "messages": [
            {"role": "system",
             "content": CANONICAL_BUFFY + "\n\nYou are the AI agent behind Freebuff. Keep it brief."},
            {"role": "user", "content": args.message or "Say hi in one short sentence."},
        ],
        "stream": False,
        "max_tokens": 200,
        "codebuff_metadata": {
            "run_id": run_id or f"run-{secrets.token_hex(6)}",
            "client_id": f"cli-{secrets.token_hex(6)}",
            "cost_mode": "free",
            **({"freebuff_instance_id": instance_id} if instance_id else {}),
        },
        "provider": {"data_collection": "deny"},
    }
    print(f"📡 POST /api/v1/chat/completions (model={model}, stream=False, run_id={run_id})…")
    status, data, _ = _http("POST", "/api/v1/chat/completions", body, chat_headers)
    print(f"→ HTTP {status}")
    if status == 200 and isinstance(data, dict):
        msg = data.get("choices", [{}])[0].get("message", {})
        print(f"✅ 回复: {msg.get('content', '')[:500]}")
        if msg.get("reasoning_content"):
            print(f"🧠 reasoning: {msg['reasoning_content'][:200]}")
        print(f"   usage: {data.get('usage')}")
        # 清理 run
        _http("POST", "/api/v1/agent-runs", {"action": "FINISH", "runId": run_id}, headers)
    else:
        print(json.dumps(data, indent=2, ensure_ascii=False)[:1500] if data else "(空响应)")
        # 清理 run
        if run_id:
            _http("POST", "/api/v1/agent-runs", {"action": "CANCEL", "runId": run_id}, headers)


def cmd_quota(_args):
    tok = get_token()
    if not tok:
        print("❌ 未找到 authToken")
        sys.exit(1)
    status, data, _ = _http("POST", "/api/v1/usage", {"fingerprintId": "cli-usage"},
                            headers={"Authorization": f"Bearer {tok}"})
    print(f"📡 HTTP {status}")
    print(json.dumps(data, indent=2, ensure_ascii=False) if data else "(空响应)")


def _all_tokens():
    """返回 [(key, token, email)]：优先读取 credentials.json 里的全部账号；未配置则用环境变量。"""
    tok = os.environ.get("FREEBUFF_TOKEN")
    if tok:
        return [("env", tok, "环境变量")]
    if CRED_FILE.exists():
        try:
            cred = json.loads(CRED_FILE.read_text())
        except Exception:
            cred = {}
        accts = cred.get("accounts")
        if isinstance(accts, dict) and accts:
            return [(k, u.get("authToken", ""), u.get("email", "?")) for k, u in accts.items() if u.get("authToken")]
        if isinstance(cred.get("default"), dict) and cred["default"].get("authToken"):
            return [("default", cred["default"]["authToken"], cred["default"].get("email", "?"))]
        if cred.get("authToken"):
            return [("default", cred["authToken"], cred.get("email", "?"))]
    return []


def _format_quota(rate_limits):
    """格式化只读 GET /session 返回的额度快照。

    优先显示 Premium/Luna 等有明确 limit 的模型；如果上游只返回一个模型，
    也照常显示。不会发起 POST，因此不创建 session、不消耗额度。
    """
    if not isinstance(rate_limits, dict) or not rate_limits:
        return "额度未知（上游未返回 rateLimitsByModel）"
    rows = []
    for model, info in rate_limits.items():
        if not isinstance(info, dict):
            continue
        rc = info.get("recentCount")
        lim = info.get("limit")
        if rc is None or lim is None:
            continue
        reset = info.get("resetAt") or info.get("reset_at")
        text = f"{model}={rc}/{lim}"
        if reset:
            text += f"，reset={reset}"
        rows.append(text)
    return "额度 " + "；".join(rows) if rows else "额度未知（快照字段不完整）"


def _check_one(tok):
    """测活。GET /api/v1/freebuff/session 是 0 消耗探测（不创建 session），
    一次调用同时判定：token 失效 / 被封禁 / 地区受限 / 额度用完 / 存活。
    官方源码 freebuff-session-api.ts 判定：
    - 正常账号：200（有 session）或 404（无 session）
    - 被封账号：403 + {"status":"banned"}（Terminal，不可恢复）
    - token 无效：401
    - 额度用完：429 或 status=rate_limited
    返回 (verdict, detail)。"""
    headers = {
        "Authorization": f"Bearer {tok}",
        # 官方只读额度快照提示：不创建 session、不消耗额度。
        "x-freebuff-include-unused-rate-limits": "1",
    }
    status, data, _ = _http("GET", "/api/v1/freebuff/session", headers=headers,
                            timeout=REQUEST_TIMEOUT)
    if status is None:
        return "网络错误", f"请求失败: {data.get('error') if isinstance(data, dict) else data}"
    if status == 401:
        return "token 失效 ❌", "HTTP 401（authToken 无效或已被撤销，不是封号）"
    if status == 403:
        # 403 + banned = 封号；403 + country_blocked = 地区受限；其他 403 也提示
        if isinstance(data, dict):
            st = data.get("status")
            if st == "banned":
                return "已被封禁 ❌", "HTTP 403 + status=banned（官方语义：Terminal，账号不可恢复，可邮件 support@codebuff.com 申诉）"
            if st == "country_blocked":
                return "地区受限 ⚠️", "HTTP 403 + status=country_blocked（当前出口 IP 非美国）"
        return "访问被拒 ⚠️", f"HTTP 403: {str(data)[:200]}"
    if status == 429:
        quota_str = _format_quota(data.get("rateLimitsByModel")) if isinstance(data, dict) else "额度未知（429 未返回额度快照）"
        return "额度用完 ⚠️", f"HTTP 429（当天 session 额度已用完，等 reset），{quota_str}"
    if status == 404:
        # 404 只代表当前没有 active session。部分上游版本会把额度快照
        # 放在错误响应 JSON 中，若有就照常显示。
        quota_str = _format_quota(data.get("rateLimitsByModel")) if isinstance(data, dict) else "额度未知（404 未返回额度快照）"
        return "存活（无活跃 session）✅", f"HTTP 404（无 session，账号可用），{quota_str}"
    if not isinstance(data, dict):
        return "未知", f"HTTP {status}: {str(data)[:200]}"
    st = data.get("status")
    if st == "banned":
        return "已被封禁 ❌", "官方语义：Terminal，账号不可恢复（可邮件 support@codebuff.com 申诉）"
    # 测活：解析存活状态 + 额度
    if st == "active":
        model = data.get("model", "?")
        tier = data.get("accessTier", "?")
        quota_str = _format_quota(data.get("rateLimitsByModel"))
        if quota_str:
            quota_str = "，" + quota_str
        return "存活 ✅", f"session active, model={model}, tier={tier}{quota_str}"
    if st in ("none", "ended"):
        quota_str = _format_quota(data.get("rateLimitsByModel"))
        if st == "ended":
            detail = "当前 session 已结束，账号仍可用"
            verdict = "存活（session 已结束）✅"
        else:
            detail = "0 消耗探测正常，账号可用"
            verdict = "存活（无活跃 session）✅"
        if quota_str:
            detail += f"，{quota_str}"
        return verdict, detail
    if st == "country_blocked":
        return "地区受限 ⚠️", "当前出口 IP 非美国（freebuff 免费模型限 US）"
    if st == "model_locked":
        quota_str = _format_quota(data.get("rateLimitsByModel"))
        return "存活（session 被锁定）⚠️", f"另一模型 session 占用中，稍后自动释放，{quota_str}"
    if st == "rate_limited":
        quota_str = _format_quota(data.get("rateLimitsByModel"))
        return "额度用完 ⚠️", f"当天 session 额度已用完，等 reset，{quota_str}"
    if st == "ip_capped":
        return "存活（IP 并发达上限）⚠️", "当前出口 IP 活跃用户过多，稍后重试"
    return "存活 ✅", f"HTTP {status}, status={st}"


def cmd_export(_args):
    """汇总全部账号的 FREEBUFF_TOKEN，一行一个，方便复制进 CF Workers 变量。"""
    pairs = _all_tokens()
    if not pairs:
        print("❌ 未找到 authToken（先运行 login 或设置 FREEBUFF_TOKEN）")
        sys.exit(1)
    print("# freebuff2api CF Workers 变量 FREEBUFF_TOKEN（一行一个账号）")
    print("# 共 %d 个账号，复制下面的行到 Cloudflare → 变量 → FREEBUFF_TOKEN" % len(pairs))
    print("# 注意：本输出含敏感 token，请勿泄露/提交到 git")
    print("=" * 60)
    for _key, tok, _email in pairs:
        print(tok)
    print("=" * 60)
    return 0


# ---------------------------------------------------------------------------

def main():
    p = argparse.ArgumentParser(description="Freebuff authToken 提取工具")
    sub = p.add_subparsers(dest="cmd", required=True)

    p_login = sub.add_parser("login", help="开始登录（生成 URL + 轮询拿 token）")
    p_login.add_argument("--fingerprint", help="指定 fingerprintId（默认自动生成）")

    sub.add_parser("tgsend", help="测试 TG 连通性（发一条测试消息）")

    sub.add_parser("show", help="显示已保存凭证并验证")
    p_sess = sub.add_parser("session", help="开/查 session")
    p_sess.add_argument("--model", default=MODEL_DEFAULT)
    p_sess.add_argument("--post", action="store_true", help="POST 开 session（默认 GET）")

    p_chat = sub.add_parser("chat", help="发一条消息测试模型 API")
    p_chat.add_argument("message", nargs="?", default=None)
    p_chat.add_argument("--model", default=MODEL_DEFAULT)
    p_chat.add_argument("--agent", default=None, help="START run 用的 agentId（默认按模型自动映射）")
    p_chat.add_argument("--run-id", default=None, help="指定 run_id（默认 START 一个）")
    p_chat.add_argument("--force", action="store_true", help="session/run 失败也直发 chat")

    sub.add_parser("quota", help="查用量")

    sub.add_parser("export", help="汇总全部账号 token，一行一个，复制进 CF Workers 变量")

    args = p.parse_args()
    {
        "login": cmd_login,
        "show": cmd_show,
        "session": cmd_session,
        "chat": cmd_chat,
        "quota": cmd_quota,
        "tgsend": cmd_tgsend,
        "export": cmd_export,
    }[args.cmd](args)


if __name__ == "__main__":
    main()
