using Bcv.Shared;
using HtmlAgilityPack;
using Supabase;
using System.Net.Http;
using System.Text;

namespace Bcv.Worker
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly Supabase.Client _supabase;
        private readonly IConfiguration _config;
        private readonly IHttpClientFactory _httpClientFactory; // 1. Usar Factory
        private TasaBcv? _ultimaTasaLocal;

        // Control de notificaciones
        private bool _notificadoHoy = false;
        private int _ultimoDiaProcesado = -1;

        public Worker(
            ILogger<Worker> logger, 
            Supabase.Client supabase, 
            IConfiguration config,
            IHttpClientFactory httpClientFactory) // Inyección del Factory
        {
            _logger = logger;
            _supabase = supabase;
            _config = config;
            _httpClientFactory = httpClientFactory;
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await EnviarTelegram("🛑 *El servicio BCV se ha cerrado o está inactivo.*");
            await base.StopAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // 2. Notificar inicio ANTES de cualquier operación pesada
            await EnviarTelegram("🚀 *Servicio BCV Iniciado correctamente.*");
            
            _logger.LogInformation("Worker iniciado. Sincronizando datos...");

            try
            {
                var respuesta = await _supabase.From<TasaBcv>()
                    .Order("creado_el", Postgrest.Constants.Ordering.Descending)
                    .Limit(1).Get();

                _ultimaTasaLocal = respuesta.Models.FirstOrDefault();
                
                if (_ultimaTasaLocal != null)
                {
                     _logger.LogInformation("Última tasa en DB: USD {usd} ({fecha})", 
                        _ultimaTasaLocal.Usd, _ultimaTasaLocal.FechaValor);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("No se pudo conectar a Supabase al inicio: {msg}", ex.Message);
                _ultimaTasaLocal = LeerRespaldoLocal();
            }

            // Primera ejecución
            await ProcesarTasas();

            while (!stoppingToken.IsCancellationRequested)
            {
                var horaVzla = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
                               TimeZoneInfo.FindSystemTimeZoneById("Venezuela Standard Time"));

                if (horaVzla.Day != _ultimoDiaProcesado)
                {
                    _notificadoHoy = false;
                    _ultimoDiaProcesado = horaVzla.Day;
                }

                if (EsHorarioPermitido(horaVzla) && !_notificadoHoy)
                {
                    string mensajeReporte = $"⏰ Son las {horaVzla:hh:mm tt}, realizando consulta...";
                    await ProcesarTasas(mensajeReporte);
                    await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                }
                else
                {
                    if (_notificadoHoy && EsHorarioPermitido(horaVzla))
                    {
                        _logger.LogInformation("Tasas ya actualizadas hoy. Saltando consultas restantes.");
                    }
                    await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
                }
            }
        }

        private bool EsHorarioPermitido(DateTime horaVzla)
        {
            bool esDiaLaboral = horaVzla.DayOfWeek != DayOfWeek.Saturday && horaVzla.DayOfWeek != DayOfWeek.Sunday;
            return esDiaLaboral && horaVzla.Hour >= 17 && horaVzla.Hour <= 20;
        }

        private async Task ProcesarTasas(string? mensajeContexto = null)
        {
            _logger.LogInformation("Consultando BCV...");

            try
            {
                // 3. Solución al Deadlock/Bloqueo:
                // En lugar de HtmlWeb.Load (síncrono), usamos HttpClient asíncrono.
                var client = _httpClientFactory.CreateClient("BcvClient");
                
                // Descargamos el HTML como string (Async real)
                var htmlContent = await client.GetStringAsync("https://www.bcv.org.ve/");
                
                // Cargamos el string en HtmlAgilityPack en memoria (es rapidísimo y seguro)
                var oDoc = new HtmlDocument();
                oDoc.LoadHtml(htmlContent);

                var tasaActual = new TasaBcv
                {
                    FechaValor = ExtraerTexto(oDoc, "//span[@class='date-display-single']"),
                    Usd = LimpiarYConvertir(ExtraerTexto(oDoc, "//div[@id='dolar']//strong")),
                    Eur = LimpiarYConvertir(ExtraerTexto(oDoc, "//div[@id='euro']//strong")),
                    CreadoEl = DateTime.UtcNow
                };

                // Lógica de detección de cambios...
                bool tasaValida = tasaActual.Usd > 0;
                
                if (!tasaValida)
                {
                    _logger.LogWarning("Se descargó la página pero no se encontraron valores (posible cambio de diseño).");
                    return;
                }

                bool huboCambio = (_ultimaTasaLocal == null || 
                                   tasaActual.Usd != _ultimaTasaLocal.Usd || 
                                   tasaActual.FechaValor != _ultimaTasaLocal.FechaValor);

                if (huboCambio)
                {
                    _logger.LogInformation("¡Cambio detectado! USD: {usd}", tasaActual.Usd);
                    
                    // Guardar DB
                    await _supabase.From<TasaBcv>().Insert(tasaActual);
                    
                    // Guardar Local
                    GuardarRespaldoLocal(tasaActual);
                    
                    // Actualizar memoria
                    _ultimaTasaLocal = tasaActual;
                    _notificadoHoy = true;

                    await EnviarTelegram($"{(mensajeContexto ?? "✅ Tasas Actualizadas")}\n\n" +
                                         $"💵 *USD:* {tasaActual.Usd}\n" +
                                         $"💶 *EUR:* {tasaActual.Eur}\n" +
                                         $"📅 *Fecha BCV:* {tasaActual.FechaValor}");
                }
                else if (!string.IsNullOrEmpty(mensajeContexto))
                {
                    await EnviarTelegram($"{mensajeContexto}\n\nℹ️ *Tasas aún no actualizadas en la página.*");
                }
                else 
                {
                    _logger.LogInformation("Sin cambios detectados.");
                }
            }
            catch (TaskCanceledException)
            {
                _logger.LogError("Timeout al intentar conectar con el BCV.");
            }
            catch (HttpRequestException httpEx)
            {
                _logger.LogError("Error de red al conectar con BCV: {msg}", httpEx.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error general procesando tasas: {msg}", ex.Message);
            }
        }

        private async Task EnviarTelegram(string mensaje)
        {
            try
            {
                string token = _config["Telegram:Token"] ?? "";
                string chatId = _config["Telegram:ChatId"] ?? "";

                if (string.IsNullOrEmpty(token)) return;

                // 4. Usar Factory también para Telegram
                var client = _httpClientFactory.CreateClient("TelegramClient");
                
                var content = new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(new 
                    { 
                        chat_id = chatId, 
                        text = mensaje, 
                        parse_mode = "Markdown" 
                    }),
                    Encoding.UTF8, 
                    "application/json");

                // Usamos POST en lugar de GET por URL larga y seguridad
                var response = await client.PostAsync($"https://api.telegram.org/bot{token}/sendMessage", content);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Telegram devolvió error: {code}", response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("No se pudo enviar mensaje a Telegram: {msg}", ex.Message);
            }
        }

        // ... Métodos auxiliares (ExtraerTexto, LimpiarYConvertir, GuardarRespaldoLocal, LeerRespaldoLocal) se mantienen igual ...
        private string ExtraerTexto(HtmlDocument doc, string xpath) =>
             doc.DocumentNode.SelectSingleNode(xpath)?.InnerText.Trim() ?? "N/D";

        private decimal LimpiarYConvertir(string valor)
        {
            if (string.IsNullOrEmpty(valor) || valor == "N/D") return 0;
            string limpio = valor.Replace(".", "").Replace(",", ".");
            return decimal.TryParse(limpio, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal res) ? res : 0;
        }

        private void GuardarRespaldoLocal(TasaBcv tasa)
        {
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "ultima_tasa.txt");
                File.WriteAllText(path, $"{tasa.Usd}|{tasa.Eur}|{tasa.FechaValor}");
            }
            catch (Exception ex) { _logger.LogError("Error guardando local: {msg}", ex.Message); }
        }

        private TasaBcv? LeerRespaldoLocal()
        {
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "ultima_tasa.txt");
                if (!File.Exists(path)) return null;
                string[] datos = File.ReadAllText(path).Split('|');
                return new TasaBcv { Usd = decimal.Parse(datos[0]), Eur = decimal.Parse(datos[1]), FechaValor = datos[2] };
            }
            catch { return null; }
        }
    }
}



