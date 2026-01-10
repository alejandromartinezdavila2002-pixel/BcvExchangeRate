using Bcv.Worker;
using Supabase;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;

// 1. CONFIGURACIÓN DEL HOST
var options = new HostApplicationBuilderSettings
{
    ContentRootPath = AppContext.BaseDirectory,
    Args = args
};

var builder = Host.CreateApplicationBuilder(options);

// 2. CONFIGURACIÓN
// Mantenemos tu lógica de limpiar y recargar para asegurar prioridad del JSON
builder.Configuration.Sources.Clear();
builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
// Agregamos esto por si acaso usas modo desarrollo local, no hace daño
builder.Configuration.AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true);

// 3. SERVICIO DE WINDOWS
builder.Services.AddWindowsService(opts =>
{
    opts.ServiceName = "BCV Exchange Rate Service";
});

// 4. VALIDACIÓN PREVIA (Evitar que arranque si faltan datos)
var supabaseUrl = builder.Configuration["Supabase:Url"];
var supabaseKey = builder.Configuration["Supabase:Key"];
var telegramToken = builder.Configuration["Telegram:Token"];

if (string.IsNullOrEmpty(supabaseUrl) || string.IsNullOrEmpty(supabaseKey) || string.IsNullOrEmpty(telegramToken))
{
    // Escribir en un archivo de log de emergencia si falla el arranque
    File.AppendAllText("startup_error.log", $"[{DateTime.Now}] Faltan credenciales en appsettings.json\n");
    throw new InvalidOperationException("Faltan configuraciones críticas (Supabase o Telegram).");
}

// 5. REGISTRO DE SERVICIOS
builder.Services.AddSingleton(_ => new Supabase.Client(supabaseUrl, supabaseKey));

builder.Services.AddHttpClient("BcvClient", client =>
{
    client.BaseAddress = new Uri("https://www.bcv.org.ve/");
    // User-Agent real para evitar bloqueos
    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
});

builder.Services.AddHttpClient("TelegramClient");

builder.Services.AddHostedService<Worker>();

// 6. CIERRE
builder.Services.Configure<HostOptions>(hostOptions =>
{
    hostOptions.ShutdownTimeout = TimeSpan.FromSeconds(10);
});

var host = builder.Build();
host.Run();