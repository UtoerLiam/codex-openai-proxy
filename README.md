# Codex OpenAI Proxy (.NET 8)

一个面向 Cursor 的 **OpenAI 公共协议适配层**：
- 下游：接收 OpenAI-compatible 请求（`/v1/responses`、`/v1/chat/completions`）
- 上游：统一转发到 Codex 上游的 `/v1/responses`

## 特性

- ✅ `POST /v1/responses`（必需）
- ✅ `POST /v1/chat/completions`（桥接到 `/v1/responses`）
- ✅ `GET /v1/models`（静态模型列表）
- ✅ 支持 `stream=true` 的 SSE 流式透传
- ✅ 控制台请求摘要日志（请求 ID / path / 状态码 / 耗时）
- ✅ 文件日志（按天切分）：`logs/app-YYYYMMDD.log`
- ✅ 敏感字段脱敏（Authorization / token）
- ✅ 默认从 `~/.codex/auth.json` 读取凭据（可通过环境变量覆盖）

## 运行

```bash
dotnet run
```

默认监听：
- `http://127.0.0.1:8181`

可选环境变量：

- `PORT`：监听端口（默认 `8181`）
- `BIND`：监听地址（默认 `127.0.0.1`）
- `CODEX_AUTH_PATH`：自定义 auth.json 路径
- `CODEX_UPSTREAM_BASE_URL`：上游 API Base URL（默认 `https://api.openai.com`）
- `CODEX_UPSTREAM_BEARER`：直接指定上游 Bearer Token（优先级高于 auth.json）

## auth.json 读取规则

程序会从 auth.json 中递归查找第一个可用 token 字段，支持：
- `token`
- `api_key`
- `apiKey`
- `access_token`

若找不到 token，程序会启动失败并返回非 0 退出码。

## Cursor 联调

在 Cursor 中配置：
- Base URL: `http://127.0.0.1:8181`
- 使用端点：`/v1/responses`（推荐）或 `/v1/chat/completions`

## curl 示例

### 非流式 `/v1/responses`

```bash
curl -X POST http://127.0.0.1:8181/v1/responses \
  -H "Content-Type: application/json" \
  -d '{
    "model": "gpt-4.1",
    "input": [{"role": "user", "content": "hello"}],
    "stream": false
  }'
```

### 流式 `/v1/responses`

```bash
curl -N -X POST http://127.0.0.1:8181/v1/responses \
  -H "Content-Type: application/json" \
  -d '{
    "model": "gpt-4.1",
    "input": [{"role": "user", "content": "write a quick hello world"}],
    "stream": true
  }'
```

### `/v1/chat/completions` 桥接

```bash
curl -X POST http://127.0.0.1:8181/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "gpt-4.1",
    "messages": [
      {"role": "system", "content": "You are helpful."},
      {"role": "user", "content": "Ping"}
    ],
    "stream": false
  }'
```

## 项目结构

- `Program.cs`：Minimal API 启动、日志中间件、路由注册
- `AuthLoader.cs`：加载并解析 Codex 凭据
- `ModelMapper.cs`：模型名映射
- `ProxyService.cs`：请求改写与上游转发（stream/non-stream）

