using log4net.Config;
using log4net;

public static class Log4netExtensions
{
    // Historical log path of the main node. Kept as the default so nodes without a
    // Logging:Directory override (i.e. main) behave exactly as before.
    private const string DefaultLogDirectory = "/home/ticolineaplay/logs";

    public static void AddLog4net(this IServiceCollection services, IConfiguration configuration)
    {
        // The appender's <file> in log4net.config is a PatternString using %property{LogDir},
        // so this property MUST be set before XmlConfigurator.Configure runs. Provider nodes
        // override the directory via Logging:Directory (e.g. /srv/<slug>/logs).
        var logDir = configuration["Logging:Directory"];
        if (string.IsNullOrWhiteSpace(logDir))
            logDir = DefaultLogDirectory;
        GlobalContext.Properties["LogDir"] = logDir.TrimEnd('/');

        XmlConfigurator.Configure(new FileInfo("log4net.config"));
        services.AddSingleton(LogManager.GetLogger(typeof(Program)));
    }
}
