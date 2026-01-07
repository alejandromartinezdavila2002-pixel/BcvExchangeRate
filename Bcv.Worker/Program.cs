using Bcv.Worker;
using Supabase;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text;
using Microsoft.Extensions.Hosting.WindowsServices;

// 1. CONFIGURACIÓN DEL HOST CON RUTA DINÁMICA
// AppContext.BaseDirectory es clave para que Azure y Windows encuentren los archivos de configuración.
var options = new HostApplicationBuilderSettings
{
    ContentRootPath = AppContext.BaseDirectory,
    Args = args
};

var builder = Host.CreateApplicationBuilder(options);

// 2. CONFIGURACIÓN DEL SERVICIO (Compatible con Windows y Linux/Azure)
// AddWindowsService es ignorado de forma segura si corres en Linux (Azure App Service/Containers).
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "BCV Exchange Rate Service";
});

// 3. CONFIGURACIÓN DE SUPABASE
var supabaseUrl = builder.Configuration["Supabase:Url"];
var supabaseKey = builder.Configuration["Supabase:Key"];

if (string.IsNullOrEmpty(supabaseUrl) || string.IsNullOrEmpty(supabaseKey))
{
    throw new InvalidOperationException("Falta la configuración de Supabase en appsettings.json o Variables de Entorno.");
}

// Registro del cliente de Base de Datos
builder.Services.AddSingleton(_ => new Supabase.Client(supabaseUrl, supabaseKey));

// 4. REGISTRO DE CLIENTES HTTP (Protección Anti-Bloqueo)
builder.Services.AddHttpClient("BcvClient", client =>
{
    client.BaseAddress = new Uri("https://www.bcv.org.ve/");
    client.Timeout = TimeSpan.FromSeconds(30);

    // Encabezados para que el BCV crea que somos un usuario real en Chrome
    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
    client.DefaultRequestHeaders.Add("Accept-Language", "es-ES,es;q=0.9,en;q=0.8");
    client.DefaultRequestHeaders.Add("Cache-Control", "no-cache");
});

builder.Services.AddHttpClient("TelegramClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
});

// 5. REGISTRO DEL WORKER PRINCIPAL
builder.Services.AddHostedService<Worker>();

// 6. CONFIGURACIÓN DE CIERRE SEGURO
builder.Services.Configure<HostOptions>(hostOptions =>
{
    // Damos 20 segundos para que el mensaje de "Servicio Detenido" logre salir a Telegram
    hostOptions.ShutdownTimeout = TimeSpan.FromSeconds(20);
});

var host = builder.Build();
host.Run();