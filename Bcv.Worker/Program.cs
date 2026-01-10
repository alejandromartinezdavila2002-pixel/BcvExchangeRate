using Bcv.Worker;
using Supabase;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;

// 1. CONFIGURACIÓN DEL HOST CON RUTA DINÁMICA
var options = new HostApplicationBuilderSettings
{
    ContentRootPath = AppContext.BaseDirectory,
    Args = args
};

var builder = Host.CreateApplicationBuilder(options);

// --- 2. LIMPIEZA TOTAL DE CONFIGURACIÓN (IGUAL QUE EN LA API) ---
// Esto ignora variables de entorno de Windows y secretos viejos
builder.Configuration.Sources.Clear();
builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

// 3. CONFIGURACIÓN DEL SERVICIO
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "BCV Exchange Rate Service";
});

// 4. VALIDACIÓN Y DIAGNÓSTICO
var supabaseUrl = builder.Configuration["Supabase:Url"];
var supabaseKey = builder.Configuration["Supabase:Key"];
var telegramToken = builder.Configuration["Telegram:Token"];

Console.WriteLine("==========================================");
Console.WriteLine("🔍 DIAGNÓSTICO DE BCV.WORKER");
if (!string.IsNullOrEmpty(telegramToken))
{
    // Confirmamos que cargue el 8515... (@BcvWorker_bot)
    Console.WriteLine($"🤖 TOKEN TRABAJADOR: {telegramToken.Substring(0, 10)}...");
}
else
{
    Console.WriteLine("❌ ERROR: No se encontró el Token en appsettings.json del Worker.");
}
Console.WriteLine("==========================================");

if (string.IsNullOrEmpty(supabaseUrl) || string.IsNullOrEmpty(supabaseKey))
{
    throw new InvalidOperationException("Falta la configuración de Supabase.");
}

if (string.IsNullOrEmpty(telegramToken))
{
    throw new InvalidOperationException("Falta el Token de Telegram del Worker.");
}

// 5. REGISTRO DE CLIENTE SUPABASE
builder.Services.AddSingleton(_ => new Supabase.Client(supabaseUrl, supabaseKey));

// 6. REGISTRO DE CLIENTES HTTP (Protección Anti-Bloqueo)
builder.Services.AddHttpClient("BcvClient", client =>
{
    client.BaseAddress = new Uri("https://www.bcv.org.ve/");
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
});

builder.Services.AddHttpClient("TelegramClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

// 7. REGISTRO DEL WORKER PRINCIPAL
builder.Services.AddHostedService<Worker>();

// 8. CONFIGURACIÓN DE CIERRE SEGURO
builder.Services.Configure<HostOptions>(hostOptions =>
{
    hostOptions.ShutdownTimeout = TimeSpan.FromSeconds(20);
});

var host = builder.Build();
host.Run();