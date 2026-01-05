using Postgrest.Attributes;
using Postgrest.Models;
using System.Text.Json.Serialization;

namespace Bcv.Shared
{
    [Table("bcvtasas")]
    // Esta línea es el truco: le dice al sistema que solo serialice los datos de TasaBcv
    [JsonDerivedType(typeof(TasaBcv))]
    public class TasaBcv : BaseModel
    {
        [PrimaryKey("id", false)]
        public long Id { get; set; }

        [Column("fecha_valor")]
        public string FechaValor { get; set; } = string.Empty;

        [Column("usd")]
        public decimal Usd { get; set; }

        [Column("eur")]
        public decimal Eur { get; set; }

        [Column("cny")]
        public decimal Cny { get; set; }

        [Column("try_val")]
        public decimal Try { get; set; }

        [Column("rub")]
        public decimal Rub { get; set; }

        [Column("creado_el")]
        public DateTime CreadoEl { get; set; } = DateTime.UtcNow;
    }
}