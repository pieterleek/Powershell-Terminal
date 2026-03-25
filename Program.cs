using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Collections.Concurrent;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<ConcurrentDictionary<string, PowerShell>>();
var app = builder.Build();


app.Use(async (context, next) =>
{
    var remoteIp = context.Connection.RemoteIpAddress?.ToString();
    
    // TODO: Vul hier specifieke IP-adressen in (bijv. localhost en je eigen huis)
    string[] allowedIps = { "127.0.0.1", "::1", "JOUW_THUIS_IP_HIER" };

    // TODO: Vul hier IP-reeksen in (Vergeet de punt op het einde niet!)
    string[] allowedRanges = { "IP_REEKS_1.", "IP_REEKS_2." };

    bool isAllowed = false;

    if (remoteIp != null)
    {
        if (allowedIps.Contains(remoteIp))
        {
            isAllowed = true;
        }
        else
        {
            foreach (var range in allowedRanges)
            {
                if (remoteIp.StartsWith(range))
                {
                    isAllowed = true;
                    break;
                }
            }
        }
    }

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
    if (!ctx.Request.Cookies.ContainsKey("PS_Auth")) return Results.Redirect("/login");
    if (!ctx.Request.Cookies.ContainsKey("PS_Name")) return Results.Redirect("/details");
    return Results.Redirect("/terminal");
});

app.MapGet("/login", (IWebHostEnvironment env) =>
{
    string path = Path.Combine(env.ContentRootPath, "wwwroot", "login.html");
    return Results.Content(File.ReadAllText(path), "text/html");
});

app.MapPost("/login", async (HttpContext ctx) =>
{
    var form = await ctx.Request.ReadFormAsync();
    
    // TODO: Verander dit naar het daadwerkelijke wachtwoord voor de studenten
    if (form["password"] == "JOUW_GEHEIME_WACHTWOORD") 
    {
        ctx.Response.Cookies.Append("PS_Auth", "true");
        return Results.Redirect("/details");
    }
    return Results.Redirect("/login");
});

app.MapGet("/details", (HttpContext ctx, IWebHostEnvironment env) =>
{
    if (!ctx.Request.Cookies.ContainsKey("PS_Auth")) return Results.Redirect("/login");

    string path = Path.Combine(env.ContentRootPath, "wwwroot", "details.html");
    return Results.Content(File.ReadAllText(path), "text/html");
});

app.MapPost("/details", async (HttpContext ctx) =>
{
    var form = await ctx.Request.ReadFormAsync();
    ctx.Response.Cookies.Append("PS_Name", form["naam"].ToString());
    ctx.Response.Cookies.Append("PS_Class", form["klas"].ToString());
    return Results.Redirect("/terminal");
});

app.MapGet("/terminal", (HttpContext ctx, IWebHostEnvironment env) =>
{
    if (!ctx.Request.Cookies.TryGetValue("PS_Name", out string? naam) ||
        !ctx.Request.Cookies.TryGetValue("PS_Class", out string? klas))
    {
        return Results.Redirect("/");
    }

    string path = Path.Combine(env.ContentRootPath, "wwwroot", "terminal.html");

    // Lees de HTML uit en vul de studentgegevens in
    string html = File.ReadAllText(path)
                      .Replace("{{naam}}", naam)
                      .Replace("{{klas}}", klas);

    return Results.Content(html, "text/html");
});

// =================================================================
// --- 3. POWERSHELL EXECUTIE & BEVEILIGING ---
// =================================================================
app.MapPost("/run", (CommandInput input, ConcurrentDictionary<string, PowerShell> sessions, HttpContext ctx) =>
{
    if (input == null || string.IsNullOrWhiteSpace(input.Command)) return Results.Ok();

    string naam = ctx.Request.Cookies["PS_Name"] ?? "Onbekend";
    string klas = ctx.Request.Cookies["PS_Class"] ?? "Onbekend";
    string studentId = $"{naam}_{klas}";

    // Beveiliging: Voorkom dat studenten in de logmap neuzen
    string cmdLower = input.Command.ToLower();
    
    // TODO: Pas de bestandsnamen aan als je logmap anders heet
    if (cmdLower.Contains("toetslog") || cmdLower.Contains("get-content c:\\t") || cmdLower.Contains("c:\\toetslogs"))
    {
        return Results.Json(new
        {
            output = "",
            errors = new[] { "Toegang geweigerd: Je mag niet in het logboek van de docent kijken! Nice try. 😉" },
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
    catch { }

    // PowerShell sessie ophalen of aanmaken
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
        var errorLines = new System.Collections.Generic.List<string>();
        var warningLines = new System.Collections.Generic.List<string>();

        // De 5-seconden killswitch tegen oneindige loops
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)); 
        using var registration = cts.Token.Register(() => 
        {
            if (ps.InvocationStateInfo.State == PSInvocationState.Running)
            {
                ps.Stop(); // Breek het PowerShell commando hard af!
            }
        });

        try
        {
            var results = ps.Invoke();

            // Controleer of de timer is afgegaan
            if (cts.IsCancellationRequested)
            {
                errorLines.Add("TIME-OUT: Je commando duurde langer dan 5 seconden en is afgebroken door de docent. Let op oneindige loops!");
            }
            else
            {
                foreach (var item in results) { if (item != null) outputText += item.ToString().TrimEnd() + "\n"; }

                if (ps.HadErrors)
                {
                    foreach (var err in ps.Streams.Error) errorLines.Add(err.ToString());
                    ps.Streams.Error.Clear();
                }

                if (ps.Streams.Warning.Count > 0)
                {
                    foreach (var warn in ps.Streams.Warning) warningLines.Add(warn.ToString());
                    ps.Streams.Warning.Clear();
                }
            }
        }
        catch (Exception ex) 
        { 
            errorLines.Add("Parse Error: " + ex.Message); 
        }

        return Results.Json(new { output = outputText, errors = errorLines, warnings = warningLines });
    }
});

app.UseStaticFiles(); 
app.UseHttpsRedirection();
app.Run();

record CommandInput(string Command);