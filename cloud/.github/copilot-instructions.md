# FactLineage Cloud development

- Use `DefaultAzureCredential` and `TokenCredential` for every Azure SDK client.
- Keep Azure resource names in Bicep parameters and runtime endpoints in `Cloud__*` configuration.
- Keep PostgreSQL as the system of record and Azure AI Search as a rebuildable current-version projection.
- Preserve keyword-only degradation when embedding generation fails.
- Protect all business HTTP and MCP endpoints with Microsoft Entra authentication; `/health` is the only anonymous endpoint.
- Follow the official [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) and [MCP documentation](https://modelcontextprotocol.io/) when changing the MCP server.