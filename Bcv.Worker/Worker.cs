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

        // Estado local
        private TasaBcv? _ultimaTasaLocal;
        private int _ultimoDiaProcesado = -1;

        // Banderas de lógica de negocio
        private bool _tasaEncontradaHoy = false;         // ¿Ya guardamos una tasa nueva hoy?
        private bool _tasaEncontradaTemprano = false;    // ¿La encontramos fuera de horario (antes de las 5)?
        private DateTime _ultimoReporteError = DateTime.MinValue; // Para no hacer spam de errores

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
            // Reporte de apagado humano
            await EnviarTelegram("🛑 *El servicio BCV se ha detenido.*\n\nMotivo: Cierre manual o reinicio del servidor. Hasta luego.", CancellationToken.None);
            await base.StopAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Worker iniciado. Sincronizando datos...");
            await Task.Delay(5000, stoppingToken);

            await CargarDatosIniciales();
            await ReportarInicio();

            while (!stoppingToken.IsCancellationRequested)
            {
                var horaVzla = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
                    TimeZoneInfo.FindSystemTimeZoneById("Venezuela Standard Time"));

                // --- A. REINICIO DE BANDERAS AL CAMBIAR DE DÍA ---
                if (horaVzla.Day != _ultimoDiaProcesado)
                {
                    _tasaEncontradaHoy = false;
                    _tasaEncontradaTemprano = false;
                    _ultimoDiaProcesado = horaVzla.Day;
                    _logger.LogInformation($"🌅 Nuevo día detectado: {horaVzla.ToShortDateString()}");
                }

                // --- B. LÓGICA DE FIN DE SEMANA (SÁBADO Y DOMINGO) ---
                // Si arrancamos el servicio un sábado/domingo, cae aquí directo.
                if (horaVzla.DayOfWeek == DayOfWeek.Saturday || horaVzla.DayOfWeek == DayOfWeek.Sunday)
                {
                    await DormirHastaElLunes(horaVzla, stoppingToken);
                    continue;
                }

                // --- C. LOGICA DE HORARIOS DIARIOS ---

                // --- C.0 LÓGICA DE FERIADO BANCARIO / FECHA FUTURA (NUEVO) ---
                // Si hoy es Lunes 12, pero la tasa dice "Martes 13", dormimos hasta el Martes 13.
                if (await VerificarSiEsFechaFutura(horaVzla, stoppingToken))
                {
                    // Si entró aquí, es porque ya durmió y acaba de despertar en el futuro.
                    // Reiniciamos el ciclo para que evalúe la nueva hora.
                    continue;
                }

                // C.1 Fuera de Horario Laboral (Noche/Madrugada)
                if (horaVzla.Hour < 7 || horaVzla.Hour >= 20)
                {
                    // CIERRE DE VIERNES A LAS 8 PM (Failsafe)
                    // Si llegamos a las 8 PM del viernes y no nos hemos ido aún (ej. porque la tasa salió temprano o no salió), nos vamos ahora.
                    if (horaVzla.DayOfWeek == DayOfWeek.Friday && horaVzla.Hour >= 20)
                    {
                        await ReportarFinDeSemana();
                        await DormirHastaElLunes(horaVzla, stoppingToken);
                        continue;
                    }

                    _logger.LogInformation("🌙 Fuera de servicio. Esperando a las 7 AM.");
                    await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
                    continue;
                }

                // C.2 Horario Preventivo (7 AM - 5 PM)
                if (horaVzla.Hour >= 7 && horaVzla.Hour < 17)
                {
                    _logger.LogInformation("👀 Modo Preventivo (7am-5pm). Consultando...");
                    await ProcesarTasas(modoIntensivo: false);

                    var lasCinco = horaVzla.Date.AddHours(17);
                    if (horaVzla.AddHours(2) > lasCinco) await Task.Delay(lasCinco - horaVzla, stoppingToken);
                    else await Task.Delay(TimeSpan.FromHours(2), stoppingToken);
                }

                // C.3 Horario Intensivo (5 PM - 8 PM)
                else if (horaVzla.Hour >= 17 && horaVzla.Hour < 20)
                {
                    // CASO 1: TAREA CUMPLIDA HOY (En horario normal)
                    // Aquí entra si acabamos de encontrar la tasa (ej. 5:40 PM)
                    if (_tasaEncontradaHoy && !_tasaEncontradaTemprano)
                    {
                        // 🔥 LÓGICA DE VIERNES FELIZ (SALIDA TEMPRANA) 🔥
                        if (horaVzla.DayOfWeek == DayOfWeek.Friday)
                        {
                            _logger.LogInformation("✅ Viernes: Tasa encontrada. Saliendo temprano.");
                            await ReportarFinDeSemana(); // Avisa y muestra resumen
                            await DormirHastaElLunes(horaVzla, stoppingToken); // Se duerme
                            continue;
                        }

                        // Lunes a Jueves: Solo descansa por hoy
                        _logger.LogInformation("✅ Tarea cumplida por hoy. Descansando.");
                        await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
                        continue;
                    }

                    // CASO 2: La encontramos temprano (ej. 2 PM) -> Seguimos vigilando suave (20 min) hasta las 8 PM
                    if (_tasaEncontradaTemprano)
                    {
                        _logger.LogInformation("⚠️ Tasa detectada temprano. Verificando cambios extra (20 min)...");
                        await ProcesarTasas(modoIntensivo: true);
                        await Task.Delay(TimeSpan.FromMinutes(20), stoppingToken);
                    }
                    // CASO 3: Aún no hay tasa -> Buscamos intensamente (10 min)
                    else
                    {
                        _logger.LogInformation("🔥 Modo Intensivo: Buscando nueva tasa (10 min)...");
                        await ProcesarTasas(modoIntensivo: true);
                        if (!_tasaEncontradaHoy) await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
                    }
                }
            }
        }

        // --- Helper para no repetir código de dormir ---
        private async Task DormirHastaElLunes(DateTime horaActual, CancellationToken ct)
        {
            _logger.LogInformation("💤 Durmiendo hasta el lunes...");
            var diasParaLunes = ((int)DayOfWeek.Monday - (int)horaActual.DayOfWeek + 7) % 7;
            if (diasParaLunes == 0) diasParaLunes = 7;

            var fechaLunes = horaActual.Date.AddDays(diasParaLunes).AddHours(7); // Lunes 7:00 AM
            var tiempoDormir = fechaLunes - horaActual;

            if (tiempoDormir.TotalMinutes > 0)
                await Task.Delay(tiempoDormir, ct);
        }

        private async Task ProcesarTasas(bool modoIntensivo)
        {
            try
            {
                var client = _httpClientFactory.CreateClient("BcvClient");
                // Timeout corto para detectar fallas rápido
                client.Timeout = TimeSpan.FromSeconds(20);
                var htmlContent = await client.GetStringAsync("https://www.bcv.org.ve/");

                var oDoc = new HtmlDocument();
                oDoc.LoadHtml(htmlContent);

                // Extracción segura
                var nuevaTasa = new TasaBcv
                {
                    FechaValor = ExtraerTexto(oDoc, "//span[@class='date-display-single']"),
                    Usd = LimpiarYConvertir(ExtraerTexto(oDoc, "//div[@id='dolar']//strong")),
                    Eur = LimpiarYConvertir(ExtraerTexto(oDoc, "//div[@id='euro']//strong")),
                    Cny = LimpiarYConvertir(ExtraerTexto(oDoc, "//div[@id='yuan']//strong")),
                    Try = LimpiarYConvertir(ExtraerTexto(oDoc, "//div[@id='lira']//strong")),
                    Rub = LimpiarYConvertir(ExtraerTexto(oDoc, "//div[@id='rublo']//strong")),
                    CreadoEl = DateTime.UtcNow
                };

                if (nuevaTasa.Usd <= 0)
                {
                    _logger.LogWarning("Lectura vacía del BCV.");
                    return;
                }

                // VALIDACIÓN: Comprobar si es diferente a lo que tenemos
                bool esNueva = false;
                if (_ultimaTasaLocal == null) esNueva = true;
                else if (nuevaTasa.FechaValor != _ultimaTasaLocal.FechaValor || nuevaTasa.Usd != _ultimaTasaLocal.Usd) esNueva = true;

                if (esNueva)
                {
                    _logger.LogInformation("🚨 ¡NUEVA TASA DETECTADA!");

                    // Insertar en BD
                    await _supabase.From<TasaBcv>().Insert(nuevaTasa);

                    // Actualizar local
                    _ultimaTasaLocal = nuevaTasa;
                    GuardarRespaldoLocal(nuevaTasa);

                    // Actualizar banderas
                    _tasaEncontradaHoy = true;

                    // Detectar si fue temprano (antes de las 5 PM)
                    var horaVzla = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Venezuela Standard Time"));
                    if (horaVzla.Hour < 17)
                    {
                        _tasaEncontradaTemprano = true;
                        await EnviarTelegram(ConstruirMensajeTasa(nuevaTasa, "⚠️ *¡ATENCIÓN! Tasa publicada TEMPRANO*"));
                    }
                    else
                    {
                        await EnviarTelegram(ConstruirMensajeTasa(nuevaTasa, "✅ *¡NUEVA TASA PUBLICADA!*"));
                    }
                }
                else
                {
                    _logger.LogInformation("Sin cambios en el BCV.");
                }

                // Si todo salió bien, reseteamos el contador de reporte de error
                _ultimoReporteError = DateTime.MinValue;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al consultar BCV");

                // LÓGICA ANTI-SPAM DE ERRORES:
                // Solo enviamos error si pasaron más de 1 hora del último reporte o nunca se reportó
                if ((DateTime.UtcNow - _ultimoReporteError).TotalMinutes > 60)
                {
                    string mensajeError = "⚠️ *Falla de comunicación con el portal BCV*\n\n";
                    mensajeError += $"Detalle: Posible caída del sitio o conexión lenta.\nError técnico: _{ex.Message}_";
                    await EnviarTelegram(mensajeError);
                    _ultimoReporteError = DateTime.UtcNow;
                }
            }
        }

        // --- MÉTODOS AUXILIARES Y REPORTES ---

        private async Task CargarDatosIniciales()
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

        private async Task ReportarInicio()
        {
            var sb = new StringBuilder();
            sb.AppendLine("🤖 *Servicio BCV Iniciado*");
            sb.AppendLine("El worker está listo para monitorear.");
            sb.AppendLine();

            if (_ultimaTasaLocal != null)
            {
                sb.AppendLine("📉 *Datos actuales en sistema:*");
                sb.AppendLine($"📅 Fecha Valor: {_ultimaTasaLocal.FechaValor}");
                sb.AppendLine($"💵 USD: {_ultimaTasaLocal.Usd}");
                sb.AppendLine($"💶 EUR: {_ultimaTasaLocal.Eur}");
            }
            else
            {
                sb.AppendLine("⚠️ No hay tasas registradas en la base de datos local.");
            }
            await EnviarTelegram(sb.ToString());
        }

        private async Task ReportarFinDeSemana()
        {
            // Solo enviamos si tenemos datos
            if (_ultimaTasaLocal == null) return;

            var sb = new StringBuilder();
            sb.AppendLine("🎉 *¡Feliz fin de semana!*");
            sb.AppendLine("El servicio entra en descanso hasta el lunes.");
            sb.AppendLine();
            sb.AppendLine("📉 *Cierre de semana (Últimas Tasas):*");
            sb.AppendLine(ConstruirCuerpoTasa(_ultimaTasaLocal));

            await EnviarTelegram(sb.ToString());
        }

        private string ConstruirMensajeTasa(TasaBcv tasa, string titulo)
        {
            var sb = new StringBuilder();
            sb.AppendLine(titulo);
            sb.AppendLine();
            sb.AppendLine(ConstruirCuerpoTasa(tasa));
            return sb.ToString();
        }

        private string ConstruirCuerpoTasa(TasaBcv tasa)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"💵 *USD:* {tasa.Usd}");
            sb.AppendLine($"💶 *EUR:* {tasa.Eur}");
            sb.AppendLine($"🇨🇳 *CNY:* {tasa.Cny}");
            sb.AppendLine($"🇹🇷 *TRY:* {tasa.Try}");
            sb.AppendLine($"🇷🇺 *RUB:* {tasa.Rub}");
            sb.AppendLine($"📅 *Fecha BCV:* {tasa.FechaValor}");
            return sb.ToString();
        }

        private async Task EnviarTelegram(string mensaje, CancellationToken ct = default)
        {
            try
            {
                string token = _config["Telegram:Token"] ?? "";
                string chatId = _config["Telegram:ChatId"] ?? "";
                if (string.IsNullOrEmpty(token)) return;

                var client = _httpClientFactory.CreateClient("TelegramClient");
                var payload = new { chat_id = chatId, text = mensaje, parse_mode = "Markdown" };
                var json = System.Text.Json.JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                await client.PostAsync($"https://api.telegram.org/bot{token}/sendMessage", content, ct);
            }
            catch (Exception ex) { _logger.LogError("Error enviando Telegram: " + ex.Message); }
        }

        // Helpers de Parsing y Archivos (Igual que antes)
        private string ExtraerTexto(HtmlDocument doc, string xpath) => doc.DocumentNode.SelectSingleNode(xpath)?.InnerText.Trim() ?? "N/D";

        private decimal LimpiarYConvertir(string valor)
        {
            if (string.IsNullOrEmpty(valor) || valor == "N/D") return 0;
            string limpio = valor.Replace(".", "").Replace(",", ".");
            return decimal.TryParse(limpio, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal res) ? res : 0;
        }

        private void GuardarRespaldoLocal(TasaBcv tasa)
        {
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ultima_tasa.txt"), $"{tasa.Usd}|{tasa.Eur}|{tasa.FechaValor}"); } catch { }
        }

        private TasaBcv? LeerRespaldoLocal()
        {
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "ultima_tasa.txt");
                if (!File.Exists(path)) return null;
                string[] d = File.ReadAllText(path).Split('|');
                return new TasaBcv { Usd = decimal.Parse(d[0]), Eur = decimal.Parse(d[1]), FechaValor = d[2] };
            }
            catch { return null; }
        }

        // --- NUEVOS MÉTODOS DE INTELIGENCIA DE FECHAS ---

        private async Task<bool> VerificarSiEsFechaFutura(DateTime horaActual, CancellationToken ct)
        {
            if (_ultimaTasaLocal == null) return false;

            // 1. Intentamos convertir el texto "Martes, 13 Enero 2026" a una fecha real
            DateTime? fechaTasa = ParsearFechaBcv(_ultimaTasaLocal.FechaValor);

            if (fechaTasa.HasValue)
            {
                // 2. Si la fecha de la tasa es MAYOR a la fecha de hoy
                // Ejemplo: Hoy es Lunes 12, Tasa es Martes 13. (13 > 12) = TRUE
                if (fechaTasa.Value.Date > horaActual.Date)
                {
                    _logger.LogInformation($"📅 Detectado Feriado/Adelanto. La tasa ya es válida para: {fechaTasa.Value.ToShortDateString()}");

                    var mensaje = $"📅 *Modo Feriado Bancario / Tasa Adelantada*\n\n" +
                                  $"El sistema detectó que la tasa actual ya aplica para el *{_ultimaTasaLocal.FechaValor}*.\n" +
                                  $"No es necesario trabajar hoy. Nos vemos el próximo día hábil.";

                    await EnviarTelegram(mensaje);

                    // 3. Dormir hasta ese día a las 7:00 AM
                    var fechaReinicio = fechaTasa.Value.Date.AddHours(7); // 7:00 AM del día de la tasa
                    var tiempoDormir = fechaReinicio - horaActual;

                    if (tiempoDormir.TotalMinutes > 0)
                    {
                        _logger.LogInformation($"💤 Durmiendo {tiempoDormir.TotalHours:F1} horas hasta la fecha valor.");
                        await Task.Delay(tiempoDormir, ct);
                        return true; // Indicamos que SÍ era fecha futura y ya dormimos
                    }
                }
            }
            return false;
        }

        private DateTime? ParsearFechaBcv(string fechaTexto)
        {
            try
            {
                // Formato esperado: "Martes, 13 Enero 2026"
                // Limpiamos espacios extra
                fechaTexto = fechaTexto.Trim();

                // Configuración regional español
                var cultura = new System.Globalization.CultureInfo("es-VE");

                // Intentamos parsear. El formato del BCV suele ser "dddd, d MMMM yyyy" o variaciones
                // Quitamos la coma si existe para facilitar
                string textoLimpio = fechaTexto.Replace(",", "");

                string[] formatos = { "dddd d MMMM yyyy", "d MMMM yyyy", "dddd dd MMMM yyyy" };

                if (DateTime.TryParseExact(textoLimpio, formatos, cultura, System.Globalization.DateTimeStyles.None, out DateTime fecha))
                {
                    return fecha;
                }

                // Fallback manual si el parseo exacto falla (por si el BCV pone espacios raros)
                var partes = textoLimpio.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // Buscamos día numérico y año (asumiendo estructura: DiaSemana DiaNum Mes Año)
                if (partes.Length >= 4)
                {
                    int dia = int.Parse(partes[1]);
                    int anio = int.Parse(partes[3]);
                    int mes = MesANumero(partes[2]);
                    return new DateTime(anio, mes, dia);
                }

                return null;
            }
            catch
            {
                _logger.LogWarning($"No se pudo interpretar la fecha BCV: {fechaTexto}");
                return null;
            }
        }

        private int MesANumero(string mes)
        {
            mes = mes.ToLower().Trim();
            return mes switch
            {
                "enero" => 1,
                "febrero" => 2,
                "marzo" => 3,
                "abril" => 4,
                "mayo" => 5,
                "junio" => 6,
                "julio" => 7,
                "agosto" => 8,
                "septiembre" => 9,
                "octubre" => 10,
                "noviembre" => 11,
                "diciembre" => 12,
                _ => 1
            };
        }
    }
}