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
                    _logger.LogInformation("Fuera de horario de publicación oficial. Esperando...");
                    await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
                }
            }
        }

        private bool EsHorarioPermitido(DateTime dt)
        {
            // Lunes a Viernes
            bool esDiaLaboral = dt.DayOfWeek != DayOfWeek.Saturday && dt.DayOfWeek != DayOfWeek.Sunday;
            // Ventana de publicación del BCV (aprox. 5pm a 8pm)
            return esDiaLaboral && dt.Hour >= 17 && dt.Hour <= 20;
        }

        private async Task ProcesarTasas()
        {
            _logger.LogInformation("Consultando BCV...");

            HtmlWeb web = new HtmlWeb
            {
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36..."
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

            // Solo guardamos si hay un cambio real o es la primera vez que arranca
            if (_ultimaTasaLocal == null || tasaActual.Usd != _ultimaTasaLocal.Usd || tasaActual.FechaValor != _ultimaTasaLocal.FechaValor)
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