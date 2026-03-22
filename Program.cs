using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Collections.Concurrent;
using System.IO;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<ConcurrentDictionary<string, PowerShell>>();
var app = builder.Build();

//Routihng
app.MapGet("/", (HttpContext ctx) =>
{
    if (!ctx.Request.Cookies.ContainsKey("PS_Auth")) return Results.Redirect("/login");
    if (!ctx.Request.Cookies.ContainsKey("PS_Name")) return Results.Redirect("/details");
    return Results.Redirect("/terminal");
});

// Login first
app.MapGet("/login", (IWebHostEnvironment env) => {
    string path = Path.Combine(env.ContentRootPath, "wwwroot", "login.html");
    return Results.Content(File.ReadAllText(path), "text/html");
});

app.MapPost("/login", async (HttpContext ctx) => {
    var form = await ctx.Request.ReadFormAsync();
    if (form["password"] == "China") { // Lekker hardcoded wachtwoord, ik weet het :P
        ctx.Response.Cookies.Append("PS_Auth", "true");
        return Results.Redirect("/details");
    }
    return Results.Redirect("/login"); 
});

// Student details
app.MapGet("/details", (HttpContext ctx, IWebHostEnvironment env) => {
    if (!ctx.Request.Cookies.ContainsKey("PS_Auth")) return Results.Redirect("/login");
    
    string path = Path.Combine(env.ContentRootPath, "wwwroot", "details.html");
    return Results.Content(File.ReadAllText(path), "text/html");
});

app.MapPost("/details", async (HttpContext ctx) => {
    var form = await ctx.Request.ReadFormAsync();
    ctx.Response.Cookies.Append("PS_Name", form["naam"].ToString());
    ctx.Response.Cookies.Append("PS_Class", form["klas"].ToString());
    return Results.Redirect("/terminal");
});

// Open Terminal
app.MapGet("/terminal", (HttpContext ctx, IWebHostEnvironment env) => {
    if (!ctx.Request.Cookies.TryGetValue("PS_Name", out string? naam) || 
        !ctx.Request.Cookies.TryGetValue("PS_Class", out string? klas)) 
    {
        return Results.Redirect("/");
    }

    string path = Path.Combine(env.ContentRootPath, "wwwroot", "terminal.html");
    
    // Lees de HTML uit en vervang de studentgegevens
    string html = File.ReadAllText(path)
                      .Replace("{{naam}}", naam)
                      .Replace("{{klas}}", klas);

    return Results.Content(html, "text/html");
});

// Run Powershell op server
app.MapPost("/run", (CommandInput input, ConcurrentDictionary<string, PowerShell> sessions, HttpContext ctx) =>
{
    if (string.IsNullOrWhiteSpace(input.Command)) return Results.Ok();

    string naam = ctx.Request.Cookies["PS_Name"] ?? "Onbekend";
    string klas = ctx.Request.Cookies["PS_Class"] ?? "Onbekend";
    string studentId = $"{naam}_{klas}";

    // Logboeks
    try
    {
        string veiligeNaam = string.Join("_", naam.Split(Path.GetInvalidFileNameChars()));
        string veiligeKlas = string.Join("_", klas.Split(Path.GetInvalidFileNameChars()));
        string logMap = @"C:\ToetsLogs"; // Ja hardcoded weet ik :)
        Directory.CreateDirectory(logMap); 
        string bestandsNaam = $"{veiligeKlas}_{veiligeNaam}.txt";
        string volledigPad = Path.Combine(logMap, bestandsNaam);
        string tijdstip = DateTime.Now.ToString("HH:mm:ss");
        string logRegel = $"[{tijdstip}] INVOER:  {input.Command}\n";
        File.AppendAllText(volledigPad, logRegel);
    }
    catch { }

    // Do magic
    var ps = sessions.GetOrAdd(studentId, id =>
    {
        var iss = InitialSessionState.CreateDefault();
        var runspace = RunspaceFactory.CreateRunspace(iss);
        runspace.Open();
        var newPs = PowerShell.Create();
        newPs.Runspace = runspace;
        return newPs;
    });

    lock (ps) 
    {
        ps.Commands.Clear();
        ps.AddScript(input.Command);
        ps.AddCommand("Out-String");

        var outputText = "";
        var errorLines = new List<string>();
        var warningLines = new List<string>();

        try
        {
            var results = ps.Invoke();
            foreach (var item in results) { if (item != null) outputText += item.ToString().TrimEnd() + "\n"; }

            if (ps.HadErrors) {
                foreach (var err in ps.Streams.Error) errorLines.Add(err.ToString());
                ps.Streams.Error.Clear();
            }

            if (ps.Streams.Warning.Count > 0) {
                foreach (var warn in ps.Streams.Warning) warningLines.Add(warn.ToString());
                ps.Streams.Warning.Clear();
            }
        }
        catch (Exception ex) { errorLines.Add("Parse Error: " + ex.Message); }

        return Results.Json(new { output = outputText, errors = errorLines, warnings = warningLines });
    }
});

// Laad ook de css bestanden in
app.UseStaticFiles();
// Alleen HTTPS
app.UseHttpsRedirection();
app.Run();

record CommandInput(string Command);