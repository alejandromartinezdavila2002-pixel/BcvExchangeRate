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

        public Worker(ILogger<Worker> logger, Supabase.Client supabase)
        {
            _logger = logger;
            _supabase = supabase;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            
            _logger.LogInformation("Worker iniciado. Sincronizando datos...");

            // 1. Intentamos sincronización inicial con la nube o local
            try
            {
                var respuesta = await _supabase.From<TasaBcv>()
                    .Order("creado_el", Postgrest.Constants.Ordering.Descending)
                    .Limit(1).Get();

                _ultimaTasaLocal = respuesta.Models.FirstOrDefault();
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Fallo conexión nube al iniciar. Buscando respaldo local...");
                _ultimaTasaLocal = LeerRespaldoLocal();
            }

            // 2. Realizamos la primera consulta al BCV de inmediato
            await ProcesarTasas();

            var horaVzla = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
               TimeZoneInfo.FindSystemTimeZoneById("Venezuela Standard Time"));

            // 3. Enviamos el reporte de inicio con las tasas actuales encontradas
            string mensajeInicio = "🚀 *Servicio BCV Iniciado*\n\n" +
                      $"💵 *USD:* {(_ultimaTasaLocal?.Usd.ToString() ?? "N/D")}\n" +
                      $"📅 *Fecha BCV:* {(_ultimaTasaLocal?.FechaValor ?? "N/D")}\n" +
                      $"⏰ *Hora Local:* {horaVzla.ToString("hh:mm tt")}\n\n" +
                      "🔍 El worker está activo y monitoreando.";

            await EnviarTelegram(mensajeInicio);

            // 4. Entramos en el bucle de espera inteligente
            while (!stoppingToken.IsCancellationRequested)
            {
                var horaVzla1 = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
                               TimeZoneInfo.FindSystemTimeZoneById("Venezuela Standard Time"));

                if (EsHorarioPermitido(horaVzla1))
                {
                    await ProcesarTasas();
                    // Esperamos 1 hora dentro del bloque de horario de publicación
                    await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                }
                else
                {
                    _logger.LogInformation("Fuera de horario (Hora Vzla: {hora}). Esperando...", horaVzla1.ToString("HH:mm"));
                    await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
                }
            }
        }

        private bool EsHorarioPermitido(DateTime dt)
        {
            // Convertimos la hora actual a la hora oficial de Venezuela
            var horaVzla = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
                           TimeZoneInfo.FindSystemTimeZoneById("Venezuela Standard Time"));

            // Lunes a Viernes
            bool esDiaLaboral = horaVzla.DayOfWeek != DayOfWeek.Saturday && horaVzla.DayOfWeek != DayOfWeek.Sunday;

            // Ventana de publicación del BCV (usando la hora de Venezuela calculada)
            return esDiaLaboral && horaVzla.Hour >= 17 && horaVzla.Hour <= 20;
        }

        private async Task ProcesarTasas()
        {
            _logger.LogInformation("Consultando BCV...");

            HtmlWeb web = new HtmlWeb
            {
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                Timeout = 30000
            };

            int reintentosMaximos = 3;
            int intentoActual = 0;
            bool exito = false;

            while (intentoActual < reintentosMaximos && !exito)
            {
                try
                {
                    intentoActual++;
                    var oDoc = await Task.Run(() => web.Load("https://www.bcv.org.ve/"));

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

                    if (_ultimaTasaLocal == null)
                    {
                        try
                        {
                            var respuesta = await _supabase.From<TasaBcv>()
                                .Order("creado_el", Postgrest.Constants.Ordering.Descending)
                                .Limit(1).Get();
                            _ultimaTasaLocal = respuesta.Models.FirstOrDefault();
                        }
                        catch
                        {
                            _ultimaTasaLocal = LeerRespaldoLocal();
                        }
                    }

                    // Comparación de cambios
                    if (tasaActual.Usd > 0 && (_ultimaTasaLocal == null || tasaActual.Usd != _ultimaTasaLocal.Usd || tasaActual.FechaValor != _ultimaTasaLocal.FechaValor))
                    {
                        _logger.LogInformation("Guardando cambio detectado: USD {usd}", tasaActual.Usd);
                        await _supabase.From<TasaBcv>().Insert(tasaActual);
                        GuardarRespaldoLocal(tasaActual);
                        _ultimaTasaLocal = tasaActual;

                        // Capturamos la hora exacta de detección en Venezuela
                        var horaDeteccion = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
                                            TimeZoneInfo.FindSystemTimeZoneById("Venezuela Standard Time"));

                        await EnviarTelegram($"✅ *Nueva Tasa BCV Detectada*\n\n" +
                                             $"💵 *USD:* {tasaActual.Usd}\n" +
                                             $"💶 *EUR:* {tasaActual.Eur}\n" +
                                             $"📅 *Fecha BCV:* {tasaActual.FechaValor}\n" +
                                             $"⏰ *Hora Detección:* {horaDeteccion.ToString("hh:mm tt")}");
                    }
                    else
                    {
                        _logger.LogWarning(">>> Sin cambios detectados <<<");
                        _logger.LogInformation("BCV actual -> USD: {usd} | EUR: {eur} | Fecha: {fecha}",
                            tasaActual.Usd, tasaActual.Eur, tasaActual.FechaValor);
                    }

                    exito = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Intento {n} fallido: {msg}", intentoActual, ex.Message);
                    if (intentoActual < reintentosMaximos)
                    {
                        await Task.Delay(10000);
                    }
                    else
                    {
                        await EnviarTelegram("⚠️ *ALERTA:* El servicio falló tras 3 intentos. Revisa la conexión.");
                    }
                }
            }
        }
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
                using var client = new HttpClient();
                // Leemos desde el archivo de configuración appsettings.json
                var config = new ConfigurationBuilder()
                    .SetBasePath(AppContext.BaseDirectory)
                    .AddJsonFile("appsettings.json")
                    .Build();

                string token = config["Telegram:Token"];
                string chatId = config["Telegram:ChatId"];

                string url = $"https://api.telegram.org/bot{token}/sendMessage?chat_id={chatId}&parse_mode=Markdown&text={Uri.EscapeDataString(mensaje)}";
                await client.GetAsync(url);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error enviando notificación a Telegram: {msg}", ex.Message);
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