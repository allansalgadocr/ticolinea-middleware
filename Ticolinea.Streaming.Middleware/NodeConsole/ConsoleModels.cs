namespace ticolinea.stream.service.NodeConsole;

public class ConsoleChannel
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public string Logo { get; set; } = "";
    public int? CategoryId { get; set; }
    public int Order { get; set; }
    public string EpgId { get; set; } = "";
    public bool Enabled { get; set; }
    /// <summary>Arrived from the panel catalog (sincronizado=1) rather than created here.</summary>
    public bool Seeded { get; set; }
}

public class ChannelInput
{
    public string? Name { get; set; }
    public string? Source { get; set; }
    public string? Logo { get; set; }
    public int? CategoryId { get; set; }
    public string? EpgId { get; set; }
    public bool Enabled { get; set; } = true;
}

public class ConsoleCategory
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Order { get; set; }
    public int ChannelCount { get; set; }
}

public class CategoryInput
{
    public string? Name { get; set; }
}

public class ConsoleUser
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = "operator";
    public bool Enabled { get; set; }
    public bool IsSeed { get; set; }
    public DateTime? LastLogin { get; set; }

    public bool IsOwner => string.Equals(Role, "owner", StringComparison.OrdinalIgnoreCase);
}

public class NewUserInput
{
    public string? Username { get; set; }
    public string? DisplayName { get; set; }
    public string? Password { get; set; }
    public string? Role { get; set; }
}

public class LoginInput
{
    public string? Username { get; set; }
    public string? Password { get; set; }
}

public class PasswordInput
{
    public string? Password { get; set; }
}
