using Microsoft.EntityFrameworkCore;

public class DraftDbContext(DbContextOptions<DraftDbContext> options) : DbContext(options)
{
    public DbSet<Draft> Drafts => Set<Draft>();
}
