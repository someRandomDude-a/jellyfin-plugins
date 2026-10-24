using System.Reflection;
using System.Text;
using MediaBrowser.Controller;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.UserManager.FileTransformation;

/// <summary>
/// Registers an index.html transformation with the optional File Transformation plugin.
/// File Transformation is loaded dynamically so this plugin has no hard dependency on it.
/// </summary>
public sealed class FileTransformationBridge : IHostedService, IDisposable
{
    public static readonly Guid TransformationId = Guid.Parse("84369a42-b7fd-4381-9df4-9dcb63b84602");

    private const string ServiceTypeName = "Jellyfin.Plugin.FileTransformation.Library.IWebFileTransformationWriteService";
    private const string ClientResourceName = "Jellyfin.Plugin.UserManager.Web.client.js";

    private readonly IServerApplicationHost _applicationHost;
    private readonly ILogger<FileTransformationBridge> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lazy<string> _clientScript;
    private object? _writeService;
    private Task? _registrationTask;

    public FileTransformationBridge(IServerApplicationHost applicationHost, ILogger<FileTransformationBridge> logger)
    {
        _applicationHost = applicationHost;
        _logger = logger;
        _clientScript = new Lazy<string>(LoadClientScript, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool Registered { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset? LastAttempt { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _registrationTask = Task.Run(() => RegisterWithRetryAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        if (_registrationTask is not null)
        {
            try
            {
                await _registrationTask.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // best effort
            }
        }

        if (Registered && _writeService is not null)
        {
            try
            {
                var remove = _writeService.GetType().GetMethod("RemoveTransformation", [typeof(Guid)]);
                remove?.Invoke(_writeService, [TransformationId]);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not remove File Transformation registration");
            }
        }
    }

    private async Task RegisterWithRetryAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 12 && !cancellationToken.IsCancellationRequested; attempt++)
        {
            LastAttempt = DateTimeOffset.UtcNow;
            try
            {
                if (TryRegister())
                {
                    Registered = true;
                    LastError = null;
                    _logger.LogInformation("Registered Jellyfin web client transformation with File Transformation");
                    return;
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _logger.LogDebug(ex, "File Transformation registration attempt {Attempt} failed", attempt);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (!Registered)
        {
            LastError ??= "File Transformation is not installed or did not become available.";
            _logger.LogWarning(
                "File Transformation plugin was not found. The native User Manager page still works, but login/account links will not be injected into the Jellyfin web client. {Error}",
                LastError);
        }
    }

    private bool TryRegister()
    {
        var serviceType = FindServiceType();
        if (serviceType is null)
        {
            LastError = "File Transformation service type not found.";
            return false;
        }

        var provider = _applicationHost.ServiceProvider;
        var service = provider?.GetService(serviceType);
        if (service is null)
        {
            LastError = "File Transformation service is not registered yet.";
            return false;
        }

        var add = serviceType.GetMethod("AddTransformation", BindingFlags.Public | BindingFlags.Instance);
        if (add is null)
        {
            LastError = "File Transformation API changed: AddTransformation was not found.";
            return false;
        }

        var parameters = add.GetParameters();
        if (parameters.Length != 3 || parameters[2].ParameterType.BaseType != typeof(MulticastDelegate))
        {
            LastError = "File Transformation API changed: unexpected AddTransformation signature.";
            return false;
        }

        var adapter = new IndexTransformer(() => _clientScript.Value, _logger);
        var adapterMethod = typeof(IndexTransformer).GetMethod(nameof(IndexTransformer.TransformAsync))
            ?? throw new MissingMethodException(nameof(IndexTransformer.TransformAsync));
        var transformation = adapterMethod.CreateDelegate(parameters[2].ParameterType, adapter);

        add.Invoke(service, [TransformationId, "index.html", transformation]);
        _writeService = service;
        return true;
    }

    private static Type? FindServiceType()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.FullName?.Contains("FileTransformation", StringComparison.OrdinalIgnoreCase) != true)
            {
                continue;
            }

            var type = assembly.GetType(ServiceTypeName, throwOnError: false);
            if (type is not null)
            {
                return type;
            }
        }

        try
        {
            var loaded = Assembly.Load("Jellyfin.Plugin.FileTransformation");
            return loaded.GetType(ServiceTypeName, throwOnError: false);
        }
        catch
        {
            return null;
        }
    }

    private static string LoadClientScript()
    {
        using var stream = typeof(FileTransformationBridge).Assembly.GetManifestResourceStream(ClientResourceName);
        if (stream is null)
        {
            throw new InvalidOperationException($"Embedded resource {ClientResourceName} was not found.");
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        _cts.Dispose();
    }

    /// <summary>
    /// Delegate target for File Transformation. The signature must match
    /// <c>Task TransformFile(string path, Stream contents)</c>.
    /// </summary>
    public sealed class IndexTransformer
    {
        private const string Marker = "<!-- injected by Jellyfin User Manager -->";

        private readonly Func<string> _scriptProvider;
        private readonly ILogger _logger;

        public IndexTransformer(Func<string> scriptProvider, ILogger logger)
        {
            _scriptProvider = scriptProvider;
            _logger = logger;
        }

        public async Task TransformAsync(string path, Stream contents)
        {
            if (!path.EndsWith("index.html", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!contents.CanRead || !contents.CanWrite || !contents.CanSeek)
            {
                _logger.LogWarning("File Transformation stream is not seekable and writable; skipping User Manager injection");
                return;
            }

            contents.Position = 0;

            string html;
            using (var reader = new StreamReader(contents, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true))
            {
                html = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            if (html.Contains(Marker, StringComparison.Ordinal))
            {
                return;
            }

            var script = _scriptProvider().Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase);
            var injection = $"{Marker}\n<script data-jellyfin-user-manager=\"1\">\n{script}\n</script>\n";

            var bodyEnd = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            html = bodyEnd >= 0
                ? html.Insert(bodyEnd, injection)
                : html + injection;

            var bytes = Encoding.UTF8.GetBytes(html);
            contents.Position = 0;
            contents.SetLength(0);
            await contents.WriteAsync(bytes).ConfigureAwait(false);
            await contents.FlushAsync().ConfigureAwait(false);
        }
    }
}
