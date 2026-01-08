using Postgrest.Attributes;
using Postgrest.Models;

namespace Bcv.Shared
{
    [Table("clientes_api")]
    public class ClienteApi : BaseModel
    {
        [Column("username_telegram")]
        public string UsernameTelegram { get; set; } = string.Empty;

        [Column("api_key")]
        public string ApiKey { get; set; } = string.Empty;

        [Column("esta_aprobado")]
        public bool EstaAprobado { get; set; }
    }
}