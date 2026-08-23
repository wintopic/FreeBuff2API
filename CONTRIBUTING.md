# 贡献指南

欢迎提交协议兼容修复、模型映射、文档、测试和 Windows 启动器改进。

## 开始之前

1. 先搜索现有 Issue 和 Pull Request，避免重复工作。
2. 为改动创建独立分支。
3. 不要提交真实 authToken、API key、邮箱、日志、.env、credentials、dist 或 Node 二进制。
4. 协议问题请记录上游 HTTP 状态码和经过脱敏的请求结构。

## 本地检查

服务端改动至少运行：

```bash
npm test
```

模型解析改动运行：

```bash
node scripts/build-freebuff-models-json.mjs freebuff-models.json
```

Windows 启动器改动在 Windows 上运行：

```powershell
dotnet build launcher/FreeBuffLauncher.csproj -c Release
```

需要生成可分发目录时，先准备官方 Node.js 运行时：

```powershell
.\scripts\prepare-launcher-runtime.ps1
dotnet publish launcher/FreeBuffLauncher.csproj -c Release -r win-x64 --self-contained true
```

真实聊天会消耗上游 session。只验证路由、鉴权和格式转换时，应优先使用 stub 或不创建 session 的接口。

## Pull Request 内容

请说明以下信息。

- 改动解决的问题
- 受影响的路由或部署方式
- 运行过的检查
- 上游行为是否有时间、账号或地区条件
- 已知没有覆盖的场景

涉及响应格式时，附上脱敏后的输入与输出示例。涉及模型可用性时，请区分模型映射存在和账号实际获得资格。

## 代码约定

- 保持 worker.js 可在 Cloudflare Worker 与 Node 入口下复用
- 不把秘密值写进源码或默认配置
- healthz 和 models 不应创建上游 session
- 多账号缓存键必须包含 token，避免跨账号复用
- 新增上游请求时考虑串行队列、超时、取消和错误语义

提交代码即表示你同意按仓库的 AGPL-3.0 许可证提供贡献。
