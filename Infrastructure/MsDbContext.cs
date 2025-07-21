using Microsoft.EntityFrameworkCore;
using SeckillSystem.Model;

namespace SeckillSystem.Infrastructure;


public class MsDbContext: DbContext
{
    public MsDbContext(DbContextOptions<MsDbContext> options) : base(options)
    {
    }

    public DbSet<Products> Products { get; set; }
}
