# Codex OpenAI Proxy (C#)

这是一个 **OpenAI Chat Completions 兼容代理**，已从 Rust 版本转换为 **C# / ASP.NET Core** 项目。

## 功能

- OpenAI 兼容接口：`/v1/chat/completions`
- 兼容直连接口：`/chat/completions`
- 模型列表接口：`/v1/models`、`/models`
- 健康检查：`/health`
- 支持流式 SSE 响应（`stream: true`）
- 启动参数支持：`--port`、`--auth-path`

## 运行

```bash
dotnet run -- --port 8888 --auth-path ~/.codex/auth.json
```

启动后默认监听：

- `http://0.0.0.0:8080`（或你通过 `--port` 指定的端口）

## 测试接口

```bash
curl http://localhost:8080/health

curl -X POST http://localhost:8080/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "gpt-5",
    "stream": false,
    "messages": [{"role": "user", "content": "hello"}]
  }'
```

## 参数

- `-p`, `--port <PORT>`: 监听端口（默认 `8080`）
- `--auth-path <PATH>`: Codex `auth.json` 路径（默认 `~/.codex/auth.json`）

## 说明

当前版本保留了代理层接口和行为骨架，便于继续扩展真实后端转发逻辑。
