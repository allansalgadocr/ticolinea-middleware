using log4net.Config;
using log4net;
using ticolinea.stream.service.Helpers;

public static class Log4netExtensions
{
    public static void AddLog4net(this IServiceCollection services, IConfiguration configuration)
    {
        // The appender's <file> in log4net.config is a PatternString using %property{LogDir},
        // so this property MUST be set before XmlConfigurator.Configure runs. Provider nodes
        // override the directory via Logging:Directory (e.g. /srv/<slug>/logs).
        // Resolution (default = historical main-node path, trailing-slash trim) lives in
        // LogTailHelper so GET /api/admin/logs reads from the exact same directory.
        var logDir = LogTailHelper.ResolveLogDirectory(configuration["Logging:Directory"]);
        GlobalContext.Properties["LogDir"] = logDir;
        // Stash for the retention janitor (static Hangfire job) — same source
        // of truth as the appender and GET /api/admin/logs.
        LogTailHelper.CurrentLogDirectory = logDir;
        LogTailHelper.CurrentRetentionDays =
            configuration.GetValue("Logging:RetentionDays", LogTailHelper.DefaultRetentionDays);

        XmlConfigurator.Configure(new FileInfo("log4net.config"));
        services.AddSingleton(LogManager.GetLogger(typeof(Program)));
    }
}
