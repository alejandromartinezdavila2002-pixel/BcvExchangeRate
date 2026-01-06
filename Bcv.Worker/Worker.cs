using Bcv.Shared;
using HtmlAgilityPack;
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
            _logger.LogInformation("Worker iniciado. Sincronizando con la base de datos en la nube...");

            // 1. Sincronización inicial: Consultamos a Supabase cuál fue el último registro real
            try
            {
                var respuesta = await _supabase.From<TasaBcv>()
                    .Order("creado_el", Postgrest.Constants.Ordering.Descending)
                    .Limit(1)
                    .Get();

                _ultimaTasaLocal = respuesta.Models.FirstOrDefault();

                if (_ultimaTasaLocal != null)
                {
                    _logger.LogInformation("Sincronización exitosa. Última tasa registrada: USD {usd} del {fecha}",
                        _ultimaTasaLocal.Usd, _ultimaTasaLocal.FechaValor);

                    //tasa de Euro
                    _logger.LogInformation("Sincronización exitosa. Última tasa registrada: EUR {eur} del {fecha}",
                        _ultimaTasaLocal.Eur, _ultimaTasaLocal.FechaValor);

                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("No se pudo obtener el último registro de la nube (posible tabla vacía): {msg}", ex.Message);
            }

            // 2. Realizamos la primera comprobación de inmediato
            await ProcesarTasas();

            // 3. Bucle de espera inteligente
            while (!stoppingToken.IsCancellationRequested)
            {
                DateTime ahora = DateTime.Now;

                if (EsHorarioPermitido(ahora))
                {
                    await ProcesarTasas();
                    // Esperamos 1 hora dentro del bloque de horario de publicación
                    await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                }
                else
                {
                    var horaVzla = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
                                   TimeZoneInfo.FindSystemTimeZoneById("Venezuela Standard Time"));

                    _logger.LogInformation("Fuera de horario (Hora Vzla: {hora}). Esperando...", horaVzla.ToString("HH:mm"));
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
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36..."
                Timeout = 30000 // 30 segundos máximo
            };

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

            /// Solo guardamos si hay un cambio real Y si la tasa extraída es válida (mayor a 0)
            if (tasaActual.Usd > 0 && (_ultimaTasaLocal == null || tasaActual.Usd != _ultimaTasaLocal.Usd || tasaActual.FechaValor != _ultimaTasaLocal.FechaValor))
            {
                _logger.LogInformation("Guardando cambio detectado: USD {usd}", tasaActual.Usd);
                await _supabase.From<TasaBcv>().Insert(tasaActual);
                _ultimaTasaLocal = tasaActual;
            }
            else
            {
                _logger.LogInformation("Sin cambios detectados.");
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
    }
}