namespace Dnn.Mcp.WebApi
{
    /// <summary>
    /// Marks an <see cref="IMcpProvider"/> whose registrations must take precedence over
    /// those of ordinary providers.
    /// </summary>
    /// <remarks>
    /// <c>McpRegistry.RegisterTool</c> assigns into a dictionary keyed by tool name, so the
    /// last provider to register a given name wins. The order
    /// <c>GetServices&lt;IMcpProvider&gt;()</c> returns providers in follows DNN's
    /// <c>IDnnStartup</c> discovery order, which a satellite assembly cannot influence — in
    /// practice a satellite assembly registers before the module's own tools and is then
    /// silently overwritten.
    ///
    /// Implementing this marker moves a provider to the end of the registration pass, which
    /// is the only supported way to replace a built-in tool with a different implementation
    /// of the same name. Providers that only add new names do not need it.
    /// </remarks>
    public interface IMcpProviderOverride : IMcpProvider
    {
    }
}
