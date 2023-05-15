using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Renci.SshNet;

public class SshTunnel
{
    private static SshTunnel _instance;
    private static readonly object _lock = new();
    private static PrivateKeyFile _privateKeyFile;
    private static string _sshHost;
    private static int _sshPort;
    private static string _sshUsername;
    private static string _dbHost;
    private static int _dbPort;

    public SshTunnel(IConfiguration configuration, ILogger logger)
    {
        if (_privateKeyFile == null || _sshHost == null || _sshUsername == null || _dbHost == null)
        {
            _sshHost = configuration["SshHost"];
            _sshPort = int.Parse(configuration["SshPort"]);
            _sshUsername = configuration["SshUsername"];
            _dbHost = configuration["DbHost"];
            _dbPort = int.Parse(configuration["DbPort"]);


            // Read the private key file content as a string
            var privateKeyFilePath = Path.Combine(Directory.GetCurrentDirectory(), "privatekey.pem");
            var privateKeyContent = File.ReadAllText(privateKeyFilePath);

            // Convert the string to a MemoryStream
            using var privateKeyStream = new MemoryStream(Encoding.UTF8.GetBytes(privateKeyContent));

            // Create a PrivateKeyFile from the MemoryStream
            _privateKeyFile = new PrivateKeyFile(privateKeyStream);
        }

        SshClient = new SshClient(_sshHost, _sshPort, _sshUsername, _privateKeyFile);
        SshClient.ErrorOccurred += (sender, e) => logger.LogError(e.Exception, "SSH error occurred");
        SshClient.HostKeyReceived += (sender, e) => logger.LogInformation($"Host key received: {e.FingerPrint}");
        SshClient.Connect();
        ForwardedPort = new ForwardedPortLocal("localhost", _dbHost, (uint)_dbPort);
        SshClient.AddForwardedPort(ForwardedPort);
        ForwardedPort.Start();
    }

    public SshClient SshClient { get; }
    public ForwardedPortLocal ForwardedPort { get; }

    public static SshTunnel GetInstance(IConfiguration configuration, ILogger logger)
    {
        if (_instance == null)
            lock (_lock)
            {
                if (_instance == null) _instance = new SshTunnel(configuration, logger);
            }

        return _instance;
    }

    ~SshTunnel()
    {
        // Clean up SSH tunnel resources when the object is destroyed
        ForwardedPort.Stop();
        SshClient.RemoveForwardedPort(ForwardedPort);
        SshClient.Disconnect();
        SshClient.Dispose();
    }
}
