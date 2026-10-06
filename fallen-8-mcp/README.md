# fallen-8-mcp

The [Fallen-8](https://www.fallen-8.com) MCP server as a .NET tool. It bridges the
[Model Context Protocol](https://modelcontextprotocol.io) to a running Fallen-8 graph database over
its REST API; it does not embed the database.

```bash
dnx fallen-8-mcp --stdio
```

That is the line the Claude Code plugin's native tier runs. It talks to the Fallen-8 at
`Fallen8Target__BaseUrl` (default `http://localhost:8080`, the port the container image listens on;
a local `dotnet run` of the API listens on `http://localhost:5000`). A secured instance needs
`Fallen8Target__ApiKey` as well. Write, admin and code tools are off until you set
`Mcp__Tools__EnableWrite`, `Mcp__Tools__EnableAdmin` or `Mcp__Tools__EnableCode` to `true`.

Without `--stdio` it starts the Streamable HTTP transport on port 8090, loopback only.

Documentation: <https://docs.fallen-8.com/mcp-server/>. Source and issues:
<https://github.com/cosh/fallen-8-core>.
