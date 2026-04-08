#!/bin/bash
# ============================================================
# Belfast Equipment Checklist – Setup & Migration Script
# ============================================================

echo "=== Belfast Coal Mine Digital Checklist Setup ==="
echo ""

# 1. Install .NET 8 SDK if needed
# https://dotnet.microsoft.com/download/dotnet/8.0

# 2. Install EF Core tools
dotnet tool install --global dotnet-ef

# 3. Restore packages
echo "Restoring packages..."
dotnet restore

# 4. Set connection string in appsettings.json or environment variable
# export ConnectionStrings__PostgreSQL="Host=localhost;Database=belfast_checklist;Username=postgres;Password=yourpassword"
# export Jwt__Key="your-secret-key-minimum-32-characters-long"

# 5. Create PostgreSQL database migration and run it
echo "Creating initial migration..."
dotnet ef migrations add InitialCreate --context ApplicationDbContext --output-dir Migrations/PostgreSQL

echo "Applying PostgreSQL migration..."
dotnet ef database update --context ApplicationDbContext

# 6. Create SQLite migration for offline
echo "Creating SQLite migration..."
dotnet ef migrations add InitialCreate --context LocalDbContext --output-dir Migrations/SQLite

echo "Applying SQLite migration..."
dotnet ef database update --context LocalDbContext

# 7. Run the application
echo ""
echo "=== Setup complete! Starting application... ==="
echo "Default admin login: admin@belfast.co.za / Admin@123"
echo ""
dotnet run
