using Microsoft.EntityFrameworkCore;

namespace FlowPlane.Infrastructure.Persistence;

public sealed class FlowPlaneDbContext(DbContextOptions<FlowPlaneDbContext> options)
    : DbContext(options);
