FROM node:24-alpine

WORKDIR /app

# 运行时需要的工具：wget 用于启动时拉取最新 worker.js
RUN apk add --no-cache wget

# 预置当前版本作为本地兜底（启动时若拉取失败仍可运行）
COPY package.json server.js worker.js ./

# 创建引导器。默认使用镜像内置 worker.js；只有显式设置
# WORKER_URL 时才在启动时拉取远程版本，失败后仍使用内置副本。
RUN printf '%s\n' \
    '#!/usr/bin/env sh' \
    '' \
    'set -e' \
    'WORKER_URL="${WORKER_URL:-}"' \
    'TMP="/tmp/worker.js"' \
    '' \
    'if [ -z "$WORKER_URL" ]; then' \
    '  echo "[entrypoint] using bundled worker.js"' \
    'else' \
    '  echo "[entrypoint] fetching worker.js from $WORKER_URL..."' \
    '  if wget -q --timeout=15 -O "$TMP" "$WORKER_URL"; then' \
    '    cp "$TMP" /app/worker.js && echo "[entrypoint] worker.js updated"' \
    '  else' \
    '    echo "[entrypoint] fetch failed, keeping bundled worker.js"' \
    '  fi' \
    'fi' \
    '' \
    'exec node /app/server.js' \
    > /app/entrypoint.sh
RUN chmod +x /app/entrypoint.sh

# Create credentials dir (mounted at runtime)
RUN mkdir -p /app/credentials && chown -R node:node /app

USER node
EXPOSE 8787

ENTRYPOINT ["/app/entrypoint.sh"]
