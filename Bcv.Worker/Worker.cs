using Bcv.Shared;
using HtmlAgilityPack;
using Supabase;
using System.Text;
using System.Net.Http;

namespace Bcv.Worker
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly Supabase.Client _supabase;
        private readonly IConfiguration _config;
        private readonly IHttpClientFactory _httpClientFactory;
        private TasaBcv? _ultimaTasaLocal;

        // Control de notificaciones para evitar spam y gestionar el descanso
        private bool _notificadoHoy = false;
        private int _ultimoDiaProcesado = -1;

        public Worker(
            ILogger<Worker> logger,
            Supabase.Client supabase,
            IConfiguration config,
            IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _supabase = supabase;
            _config = config;
            _httpClientFactory = httpClientFactory;
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogWarning("Deteniendo el servicio...");
            // Usamos CancellationToken.None para asegurar que el mensaje salga antes de que el host muera
            await EnviarTelegram("🛑 *El servicio BCV se ha cerrado o está inactivo.*", CancellationToken.None);
            await base.StopAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Worker iniciado. Sincronizando datos...");

            // 1. Sincronización inicial con Supabase o Respaldo Local
            try
            {
                var respuesta = await _supabase.From<TasaBcv>()
                    .Order("creado_el", Postgrest.Constants.Ordering.Descending)
                    .Limit(1).Get();

                _ultimaTasaLocal = respuesta.Models.FirstOrDefault();
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Fallo conexión con Supabase. Usando respaldo local: {msg}", ex.Message);
                _ultimaTasaLocal = LeerRespaldoLocal();
            }

            // 2. Reporte Detallado de Inicio
            var sbInicio = new StringBuilder();
            sbInicio.AppendLine("🚀 *Servicio BCV Iniciado*");
            if (_ultimaTasaLocal != null)
            {
                sbInicio.AppendLine("📉 *Últimos valores registrados:*");
                sbInicio.AppendLine($"💵 *USD:* {_ultimaTasaLocal.Usd}");
                sbInicio.AppendLine($"💶 *EUR:* {_ultimaTasaLocal.Eur}");
                sbInicio.AppendLine($"📅 *Fecha:* {_ultimaTasaLocal.FechaValor}");
            }
            else
            {
                sbInicio.AppendLine("⚠️ *Estado:* Sin datos previos registrados.");
            }
            await EnviarTelegram(sbInicio.ToString());

            // 3. Primera ejecución inmediata al encender
            await ProcesarTasas();

            // 4. Bucle principal de monitoreo inteligente
            while (!stoppingToken.IsCancellationRequested)
            {
                var horaVzla = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
                               TimeZoneInfo.FindSystemTimeZoneById("Venezuela Standard Time"));

                // Reinicio de bandera al cambiar el día
                if (horaVzla.Day != _ultimoDiaProcesado)
                {
                    _notificadoHoy = false;
                    _ultimoDiaProcesado = horaVzla.Day;
                }

                // --- LÓGICA DE DESCANSO (MADRUGADA / TAREA CUMPLIDA) ---
                // Si ya son más de las 5 PM y YA encontramos la tasa, dormimos hasta mañana a las 6 AM
                if (horaVzla.Hour >= 17 && _notificadoHoy)
                {
                    _logger.LogInformation("💤 Tasa del día obtenida. Entrando en modo descanso hasta mañana a las 6:00 AM.");

                    DateTime mananaSeisAM = horaVzla.Date.AddDays(1).AddHours(6);
                    TimeSpan tiempoParaDescansar = mananaSeisAM - horaVzla;

                    await Task.Delay(tiempoParaDescansar, stoppingToken);
                    continue;
                }

                // --- LÓGICA DE HORARIO INTENSIVO (5 PM a 8 PM) ---
                if (EsHorarioPermitido(horaVzla))
                {
                    if (!_notificadoHoy)
                    {
                        // Reporte inicial de las 5:00 PM (ventana de los primeros 20 min)
                        if (horaVzla.Hour == 17 && horaVzla.Minute < 20)
                        {
                            string mensajeContexto = $"⏰ *Son las {horaVzla:hh:mm tt}, iniciando monitoreo intensivo...*";
                            await ProcesarTasas(mensajeContexto);
                        }
                        else
                        {
                            // Consultas silenciosas cada 20 minutos
                            await ProcesarTasas(null);
                        }

                        if (!_notificadoHoy)
                        {
                            _logger.LogInformation("⏳ Modo Intensivo: Reintentando en 20 minutos...");
                            await Task.Delay(TimeSpan.FromMinutes(20), stoppingToken);
                        }
                    }
                }
                // --- LÓGICA PREVENTIVA (Resto del día) ---
                else
                {
                    _logger.LogInformation("ℹ️ Modo preventivo: Consulta cada 2 horas fuera de horario publicación.");
                    await ProcesarTasas(null);
                    await Task.Delay(TimeSpan.FromHours(2), stoppingToken);
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
                var client = _httpClientFactory.CreateClient("BcvClient");
                var htmlContent = await client.GetStringAsync("https://www.bcv.org.ve/");

                var oDoc = new HtmlDocument();
                oDoc.LoadHtml(htmlContent);

                // Extracción de todas las tasas disponibles en el portal del BCV
                var tasaActual = new TasaBcv
                {
                    FechaValor = ExtraerTexto(oDoc, "//span[@class='date-display-single']"),
                    Usd = LimpiarYConvertir(ExtraerTexto(oDoc, "//div[@id='dolar']//strong")),
                    Eur = LimpiarYConvertir(ExtraerTexto(oDoc, "//div[@id='euro']//strong")),
                    Cny = LimpiarYConvertir(ExtraerTexto(oDoc, "//div[@id='yuan']//strong")),
                    Try = LimpiarYConvertir(ExtraerTexto(oDoc, "//div[@id='lira']//strong")),
                    Rub = LimpiarYConvertir(ExtraerTexto(oDoc, "//div[@id='rublo']//strong")),
                    CreadoEl = DateTime.UtcNow
                };

                // Validación mínima: si no hay USD, algo falló en la descarga
                if (tasaActual.Usd <= 0)
                {
                    _logger.LogWarning("No se pudo obtener la tasa base (USD). Abortando guardado.");
                    return;
                }

                _logger.LogInformation("🔍 Tasas obtenidas -> USD: {usd} | EUR: {eur} | CNY: {cny}", tasaActual.Usd, tasaActual.Eur, tasaActual.Cny);

                // Verificamos si hubo cambios en la fecha o en el valor del dólar
                bool huboCambio = (_ultimaTasaLocal == null ||
                                   tasaActual.Usd != _ultimaTasaLocal.Usd ||
                                   tasaActual.FechaValor != _ultimaTasaLocal.FechaValor);

                var sbTelegram = new StringBuilder();
                if (!string.IsNullOrEmpty(mensajeContexto))
                {
                    sbTelegram.AppendLine(mensajeContexto);
                    sbTelegram.AppendLine();
                }

                if (huboCambio)
                {
                    _logger.LogInformation("✅ ¡Cambio detectado! Actualizando Supabase.");

                    // Insertar el objeto completo con todas las monedas
                    await _supabase.From<TasaBcv>().Insert(tasaActual);

                    GuardarRespaldoLocal(tasaActual);
                    _ultimaTasaLocal = tasaActual;
                    _notificadoHoy = true;

                    sbTelegram.AppendLine("✅ *¡Nueva Tasa Detectada!*");
                }
                else
                {
                    _logger.LogInformation("Sin cambios. Manteniendo silencio.");
                    sbTelegram.AppendLine(" *Sin cambios detectados respecto a la última tasa.*");
                }

                // Construcción del mensaje para Telegram incluyendo las nuevas monedas
                sbTelegram.AppendLine();
                sbTelegram.AppendLine($"💵 *USD:* {tasaActual.Usd}");
                sbTelegram.AppendLine($"💶 *EUR:* {tasaActual.Eur}");
                sbTelegram.AppendLine($"🇨🇳 *CNY:* {tasaActual.Cny}");
                sbTelegram.AppendLine($"🇹🇷 *TRY:* {tasaActual.Try}");
                sbTelegram.AppendLine($"🇷🇺 *RUB:* {tasaActual.Rub}");
                sbTelegram.AppendLine($"📅 *Fecha BCV:* {tasaActual.FechaValor}");

                // Envío a Telegram si hubo cambio o si es el monitoreo programado
                if (huboCambio || !string.IsNullOrEmpty(mensajeContexto))
                {
                    await EnviarTelegram(sbTelegram.ToString());
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("❌ Error en proceso de extracción: {msg}", ex.Message);
            }
        }

        private async Task EnviarTelegram(string mensaje, CancellationToken cancellationToken = default)
        {
            try
            {
                string token = _config["Telegram:Token"] ?? "";
                string chatId = _config["Telegram:ChatId"] ?? "";
                if (string.IsNullOrEmpty(token)) return;

                var client = _httpClientFactory.CreateClient("TelegramClient");
                var payload = new { chat_id = chatId, text = mensaje, parse_mode = "Markdown" };
                var content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                await client.PostAsync($"https://api.telegram.org/bot{token}/sendMessage", content, cancellationToken);
            }
            catch (Exception ex) { _logger.LogError("Fallo Telegram: {msg}", ex.Message); }
        }

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
            catch { }
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