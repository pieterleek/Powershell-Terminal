using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Collections.Concurrent;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;

var builder = WebApplication.CreateBuilder(args);


builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(4);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.Cookie.Name = "PS_Session";
});

builder.Services.AddSingleton<ConcurrentDictionary<string, PowerShell>>();

var app = builder.Build();


app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseSession();


var templateCache = new ConcurrentDictionary<string, string>();
string GetTemplate(IWebHostEnvironment env, string fileName) =>
    templateCache.GetOrAdd(fileName, name =>
        File.ReadAllText(Path.Combine(env.ContentRootPath, "wwwroot", name)));

// =================================================================
// --- 1. IP-BEVEILIGING ---
// =================================================================
app.Use(async (context, next) =>
{
    var remoteIp = context.Connection.RemoteIpAddress?.ToString();

    // TODO: Vul hier specifieke IP-adressen in (bijv. localhost en je eigen huis)
    string[] allowedIps = { "127.0.0.1", "::1", "JOUW_THUIS_IP_HIER" };

    // TODO: Vul hier IP-reeksen in (Vergeet de punt op het einde niet!)
    string[] allowedRanges = { "IP_REEKS_1.", "IP_REEKS_2." };

    bool isAllowed = remoteIp != null &&
        (allowedIps.Contains(remoteIp) || allowedRanges.Any(r => remoteIp.StartsWith(r)));

    if (!isAllowed)
    {
        context.Response.StatusCode = 403;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync("Toegang geweigerd. Deze terminal is uitsluitend bereikbaar vanaf het afgeschermde netwerk.");
        return;
    }

    await next.Invoke();
});

// =================================================================
// --- 2. ROUTING & AUTHENTICATIE ---
// =================================================================
app.MapGet("/", (HttpContext ctx) =>
{
    if (ctx.Session.GetString("PS_Auth") == null) return Results.Redirect("/login");
    if (ctx.Session.GetString("PS_Name") == null) return Results.Redirect("/details");
    return Results.Redirect("/terminal");
});

app.MapGet("/login", (IWebHostEnvironment env) =>
    Results.Content(GetTemplate(env, "login.html"), "text/html"));

app.MapPost("/login", async (HttpContext ctx, IConfiguration config) =>
{
    var form = await ctx.Request.ReadFormAsync();
    var wachtwoord = config["AppSettings:Password"] ?? Environment.GetEnvironmentVariable("PS_PASSWORD");

    if (!string.IsNullOrEmpty(wachtwoord) && form["password"] == wachtwoord)
    {
        ctx.Session.SetString("PS_Auth", "true");
        return Results.Redirect("/details");
    }
    return Results.Redirect("/login?fout=1");
});

app.MapGet("/details", (HttpContext ctx, IWebHostEnvironment env) =>
{
    if (ctx.Session.GetString("PS_Auth") == null) return Results.Redirect("/login");
    return Results.Content(GetTemplate(env, "details.html"), "text/html");
});

app.MapPost("/details", async (HttpContext ctx) =>
{
    if (ctx.Session.GetString("PS_Auth") == null) return Results.Redirect("/login");

    var form = await ctx.Request.ReadFormAsync();
    ctx.Session.SetString("PS_Name", form["naam"].ToString());
    ctx.Session.SetString("PS_Class", form["klas"].ToString());
    return Results.Redirect("/terminal");
});

app.MapGet("/terminal", (HttpContext ctx, IWebHostEnvironment env) =>
{
    if (ctx.Session.GetString("PS_Auth") == null) return Results.Redirect("/login");

    string? naam = ctx.Session.GetString("PS_Name");
    string? klas = ctx.Session.GetString("PS_Class");

    if (naam == null || klas == null) return Results.Redirect("/details");

    string html = GetTemplate(env, "terminal.html")
                      .Replace("{{naam}}", WebUtility.HtmlEncode(naam))
                      .Replace("{{klas}}", WebUtility.HtmlEncode(klas));

    return Results.Content(html, "text/html");
});


app.MapPost("/run", (CommandInput input, ConcurrentDictionary<string, PowerShell> sessions, HttpContext ctx) =>
{

    if (ctx.Session.GetString("PS_Auth") == null)
        return Results.Json(new { output = "", errors = new[] { "Niet ingelogd." }, warnings = Array.Empty<string>() });

    if (input == null || string.IsNullOrWhiteSpace(input.Command)) return Results.Ok();


    string naam = ctx.Session.GetString("PS_Name") ?? "Onbekend";
    string klas = ctx.Session.GetString("PS_Class") ?? "Onbekend";
    string studentId = $"{naam}_{klas}";

 
    string cmdLower = input.Command.ToLower();
    string[] altijdGeblokkeerd = { "invoke-expression", "iex ", "start-process", "cmd.exe", "powershell.exe", "wscript", "cscript", "mshta" };
    string[] logPadPatterns = { @"c:\toetslog", "c:/toetslog", "toetslog" };

    bool isGeblokkeerd =
        altijdGeblokkeerd.Any(p => cmdLower.Contains(p)) ||
        logPadPatterns.Any(p => cmdLower.Replace(" ", "").Contains(p));

    if (isGeblokkeerd)
    {
        return Results.Json(new
        {
            output = "",
            errors = new[] { "Toegang geweigerd: Dit commando is niet toegestaan tijdens de toets." },
            warnings = Array.Empty<string>()
        });
    }

    // Logboek wegschrijven
    try
    {
        string veiligeNaam = string.Join("_", naam.Split(Path.GetInvalidFileNameChars()));
        string veiligeKlas = string.Join("_", klas.Split(Path.GetInvalidFileNameChars()));

        // TODO: Map die verborgen is via Windows (Hidden attribuut)
        string logMap = @"C:\ToetsLogs";

        Directory.CreateDirectory(logMap);
        string bestandsNaam = $"{veiligeKlas}_{veiligeNaam}.txt";
        string volledigPad = Path.Combine(logMap, bestandsNaam);
        string tijdstip = DateTime.Now.ToString("HH:mm:ss");
        string logRegel = $"[{tijdstip}] INVOER:  {input.Command}\n";
        File.AppendAllText(volledigPad, logRegel);
    }
    catch (Exception ex)
    {
       
        Console.Error.WriteLine($"[LOG FOUT] Kon niet loggen voor {studentId}: {ex.Message}");
    }

    // PowerShell sessie ophalen of aanmaken
    var ps = sessions.GetOrAdd(studentId, id =>
    {
        var iss = InitialSessionState.CreateDefault();
        // Fix: ConstrainedLanguage blokkeert .NET-methode aanroepen zoals [IO.File]::ReadAllText()
        // Normale PowerShell-commando's (loops, variabelen, cmdlets, pipeline) werken gewoon
        iss.LanguageMode = PSLanguageMode.ConstrainedLanguage;
        var runspace = RunspaceFactory.CreateRunspace(iss);
        runspace.Open();
        var newPs = PowerShell.Create();
        newPs.Runspace = runspace;
        return newPs;
    });

    lock (ps)
    {
        ps.Commands.Clear();
        ps.Streams.Error.Clear();
        ps.Streams.Warning.Clear();
        ps.AddScript(input.Command);
        ps.AddCommand("Out-String");

        var outputText = "";
        var errorLines = new System.Collections.Generic.List<string>();
        var warningLines = new System.Collections.Generic.List<string>();

        // De 5-seconden killswitch tegen oneindige loops
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var registration = cts.Token.Register(() =>
        {
            if (ps.InvocationStateInfo.State == PSInvocationState.Running)
                ps.Stop();
        });

        try
        {
            var results = ps.Invoke();

            if (cts.IsCancellationRequested)
            {
                errorLines.Add("TIME-OUT: Je commando duurde langer dan 5 seconden en is afgebroken. Let op oneindige loops!");
            }
            else
            {
                foreach (var item in results)
                    if (item != null) outputText += item.ToString().TrimEnd() + "\n";

                if (ps.HadErrors)
                    foreach (var err in ps.Streams.Error) errorLines.Add(err.ToString());

                if (ps.Streams.Warning.Count > 0)
                    foreach (var warn in ps.Streams.Warning) warningLines.Add(warn.ToString());
            }
        }
        catch (Exception ex)
        {
            errorLines.Add("Fout: " + ex.Message);
        }

        return Results.Json(new { output = outputText, errors = errorLines, warnings = warningLines });
    }
});

app.Run();

record CommandInput(string Command);
