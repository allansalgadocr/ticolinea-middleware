using Hangfire.Dashboard;

namespace ticolinea.stream.service
{
    public class DashboardNoAuthorizationFilter : IDashboardAuthorizationFilter
    {
        public bool Authorize(DashboardContext dashboardContext)
    {
        return true;
    }
}
}
