using Bcv.Shared;
using HtmlAgilityPack;
using System.Net.Http;
using Supabase;

namespace Bcv.Worker
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly Supabase.Client _supabase;
        private TasaBcv? _ultimaTasaLocal;
        private readonly IConfiguration _config;

        // Control de notificaciones
        private bool _notificadoHoy = false;
        private int _ultimoDiaProcesado = -1;

        public Worker(ILogger<Worker> logger, Supabase.Client supabase, IConfiguration config)
        {
            _logger = logger;
            _supabase = supabase;
            _config = config; // Ahora leemos los secretos desde aquí
        }

        // Se ejecuta cuando el servicio se detiene o se cierra
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await EnviarTelegram("🛑 *El servicio BCV se ha cerrado o está inactivo.*");
            await base.StopAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Worker iniciado. Sincronizando datos...");

            try
            {
                var respuesta = await _supabase.From<TasaBcv>()
                    .Order("creado_el", Postgrest.Constants.Ordering.Descending)
                    .Limit(1).Get();

                _ultimaTasaLocal = respuesta.Models.FirstOrDefault();
            }
            catch (Exception)
            {
                _ultimaTasaLocal = LeerRespaldoLocal();
            }

            await ProcesarTasas();

            while (!stoppingToken.IsCancellationRequested)
            {
                var horaVzla = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
                               TimeZoneInfo.FindSystemTimeZoneById("Venezuela Standard Time"));

                // Reiniciar el estado de notificación al cambiar de día
                if (horaVzla.Day != _ultimoDiaProcesado)
                {
                    _notificadoHoy = false;
                    _ultimoDiaProcesado = horaVzla.Day;
                }

                // Si estamos en horario y aún no hemos encontrado/notificado las tasas de hoy
                if (EsHorarioPermitido(horaVzla) && !_notificadoHoy)
                {
                    string mensajeReporte = $"⏰ Son las {horaVzla:hh:mm tt}, realizando consulta...";
                    await ProcesarTasas(mensajeReporte);

                    // Esperar 1 hora para la siguiente consulta si aún no se ha notificado éxito
                    await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                }
                else
                {
                    // Si ya se notificó hoy, el log indicará que se ahorra la consulta
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

            HtmlWeb web = new HtmlWeb
            {
                UserAgent = "Mozilla/5.0...",
                Timeout = 30000
            };

            try
            {
                var oDoc = await Task.Run(() => web.Load("https://www.bcv.org.ve/"));
                var tasaActual = new TasaBcv
                {
                    FechaValor = ExtraerTexto(oDoc, "//span[@class='date-display-single']"),
                    Usd = LimpiarYConvertir(ExtraerTexto(oDoc, "//div[@id='dolar']//strong")),
                    Eur = LimpiarYConvertir(ExtraerTexto(oDoc, "//div[@id='euro']//strong")),
                    CreadoEl = DateTime.UtcNow
                    // ... otros campos
                };

                bool huboCambio = tasaActual.Usd > 0 &&
                    (_ultimaTasaLocal == null || tasaActual.Usd != _ultimaTasaLocal.Usd || tasaActual.FechaValor != _ultimaTasaLocal.FechaValor);

                if (huboCambio)
                {
                    await _supabase.From<TasaBcv>().Insert(tasaActual);
                    GuardarRespaldoLocal(tasaActual);
                    _ultimaTasaLocal = tasaActual;
                    _notificadoHoy = true; // <--- Bloquea futuras consultas el mismo día

                    await EnviarTelegram($"{(mensajeContexto ?? "✅ Tasas Actualizadas")}\n\n" +
                                         $"💵 *USD:* {tasaActual.Usd}\n" +
                                         $"💶 *EUR:* {tasaActual.Eur}\n" +
                                         $"📅 *Fecha BCV:* {tasaActual.FechaValor}");
                }
                else if (!string.IsNullOrEmpty(mensajeContexto))
                {
                    // Solo enviamos mensaje de "no actualizado" si venimos del bucle horario (17h-20h)
                    await EnviarTelegram($"{mensajeContexto}\n\nℹ️ *Tasas aún no actualizadas en la página.*");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Error en proceso: {msg}", ex.Message);
            }
        }

        // ... Mantener métodos ExtraerTexto, LimpiarYConvertir, EnviarTelegram, etc.
        private string ExtraerTexto(HtmlDocument doc, string xpath) =>
            doc.DocumentNode.SelectSingleNode(xpath)?.InnerText.Trim() ?? "N/D";

        private decimal LimpiarYConvertir(string valor)
        {
            if (string.IsNullOrEmpty(valor) || valor == "N/D") return 0;
            string limpio = valor.Replace(".", "").Replace(",", ".");
            return decimal.TryParse(limpio, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal res) ? res : 0;
        }

        private async Task EnviarTelegram(string mensaje)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)); // Timeout de 10 seg
                using var client = new HttpClient();

                // Lee directamente de los User Secrets o AppSettings
                string token = _config["Telegram:Token"] ?? "";
                string chatId = _config["Telegram:ChatId"] ?? "";

                if (string.IsNullOrEmpty(token)) return;

                string url = $"https://api.telegram.org/bot{token}/sendMessage?chat_id={chatId}&parse_mode=Markdown&text={Uri.EscapeDataString(mensaje)}";

                var response = await client.GetAsync(url, cts.Token);
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



