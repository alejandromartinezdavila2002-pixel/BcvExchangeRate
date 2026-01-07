using Bcv.Worker;
using Supabase;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text;
using Microsoft.Extensions.Hosting.WindowsServices;

// 1. CONFIGURACIÓN DEL HOST CON RUTA DINÁMICA
// Usamos AppContext.BaseDirectory para que el servicio siempre encuentre el appsettings.json
// aunque Windows lo ejecute desde System32.
var options = new HostApplicationBuilderSettings
{
    ContentRootPath = AppContext.BaseDirectory,
    Args = args
};

var builder = Host.CreateApplicationBuilder(options);

// 2. CONFIGURACIÓN DEL SERVICIO DE WINDOWS
builder.Services.AddWindowsService(options =>
{
    // Este es el nombre interno que usará el Service Control Manager
    options.ServiceName = "BCV Exchange Rate Service";
});

// 3. CONFIGURACIÓN DE SUPABASE
// Leemos la configuración desde el appsettings.json que ahora sí será encontrado
var supabaseUrl = builder.Configuration["Supabase:Url"];
var supabaseKey = builder.Configuration["Supabase:Key"];

// Registro del cliente como Singleton para mantener la conexión activa
builder.Services.AddSingleton(_ => new Supabase.Client(supabaseUrl!, supabaseKey!));

// 5. REGISTRO DE CLASES HTTP (NUEVO)
builder.Services.AddHttpClient("BcvClient", client =>
{
    // Imitamos un navegador para evitar bloqueos del servidor BCV
    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    client.Timeout = TimeSpan.FromSeconds(30); // Timeout explícito
});

builder.Services.AddHttpClient("TelegramClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});

// 4. REGISTRO DEL WORKER PRINCIPAL
builder.Services.AddHostedService<Worker>();

builder.Services.Configure<HostOptions>(hostOptions =>
{
    // Aumentamos el tiempo de espera de cierre de 5s (default) a 15s
    hostOptions.ShutdownTimeout = TimeSpan.FromSeconds(15);
});

var host = builder.Build();
host.Run();

