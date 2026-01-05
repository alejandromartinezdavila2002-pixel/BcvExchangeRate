using Microsoft.EntityFrameworkCore;
using Bcv.Shared;

namespace Bcv.Api.Data
{
    public class BcvDbContext : DbContext
    {
        public BcvDbContext(DbContextOptions<BcvDbContext> options) : base(options) { }

        public DbSet<TasaBcv> Tasas { get; set; }
    }
}