namespace CodingAgents.Server;

public class AppSettings
{
    public bool EnableWhatsApp { get; set; }
    public bool EnableEmail { get; set; }
    public WhatsAppConfig? WhatsApp { get; set; }
    public EmailConfig? Email { get; set; }
}

public class WhatsAppConfig
{
    public string? Phone { get; set; }
    public string? ApiKey { get; set; }
}

public class EmailConfig
{
    public string? SmtpServer { get; set; }
    public int SmtpPort { get; set; }
    public string? ImapServer { get; set; }
    public int ImapPort { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? TargetEmail { get; set; }
}
