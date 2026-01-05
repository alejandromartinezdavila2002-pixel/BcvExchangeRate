namespace Bcv.Shared
{
    public class TasaBcv
    {
        public int Id { get; set; }
        public string Moneda { get; set; } = string.Empty; // Ej: "USD"
        public decimal Valor { get; set; }
        public string FechaValorOficial { get; set; } = string.Empty; // Fecha del BCV
        public DateTime FechaCaptura { get; set; } = DateTime.UtcNow; // Fecha de guardado
    }
}