using ExpenseAssistant.Core;
using Microsoft.EntityFrameworkCore;

namespace ExpenseAssistant.Infrastructure;

public static class ExpenseDatabaseInitializer
{
    public const string DatasetVersion = "v2";
    public const string Currency = "EUR";

    public static async Task InitializeAsync(ExpenseDbContext context, CancellationToken cancellationToken = default)
    {
        await context.Database.MigrateAsync(cancellationToken);
        await using var databaseTransaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var existingCategoryIds = (await context.Categories
            .Select(category => category.Id)
            .ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var existingTransactionIds = (await context.Transactions
            .Select(transaction => transaction.Id)
            .ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);

        (string Id, string Name)[] categories =
        [
            ("groceries", "Groceries"),
            ("dining", "Dining"),
            ("transport", "Transport"),
            ("utilities", "Utilities"),
            ("software", "Software"),
            ("travel", "Travel"),
            ("shopping", "Shopping"),
            ("business-travel", "Business Travel")
        ];

        foreach (var category in categories)
        {
            if (!existingCategoryIds.Contains(category.Id))
            {
                context.Categories.Add(new Category { Id = category.Id, Name = category.Name });
            }
        }

        List<Transaction> transactions =
        [
            CreateTransaction("TX-G1", new DateOnly(2026, 3, 2), "groceries", "Green Market", "Weekly groceries", 1250),
            CreateTransaction("TX-G2", new DateOnly(2026, 3, 14), "groceries", "Green Market", "Fresh produce", 875),
            CreateTransaction("TX-G3", new DateOnly(2026, 4, 1), "groceries", "Green Market", "Pantry supplies", 500)
        ];

        AddTransactions(transactions, "E", 97,
        [
            ("groceries", "Green Market", "Seasonal fruit and vegetables", 1840),
            ("groceries", "Pantry Lane", "Rice, lentils and canned tomatoes", 2375),
            ("groceries", "Morning Bakery", "Bread rolls and sandwich loaf", 680),
            ("groceries", "Fresh Basket", "Milk, eggs and plain yogurt", 1190),
            ("groceries", "Green Market", "Pasta, olive oil and cheese", 2640),
            ("groceries", "Pantry Lane", "Tea, coffee beans and breakfast oats", 2180),
            ("dining", "Corner Cafe", "Lunch sandwich and filter coffee", 1240),
            ("dining", "Noodle Table", "Vegetable noodles and sparkling water", 1670),
            ("dining", "Stone Oven", "Pizza takeaway", 1490),
            ("dining", "Morning Bakery", "Pastry and cappuccino", 720),
            ("dining", "Garden Table", "Dinner for two", 5860),
            ("dining", "Corner Cafe", "Soup and bread lunch", 980),
            ("transport", "City Transit", "Monthly bus and tram pass", 4900),
            ("transport", "Metro Link", "Single underground fare", 280),
            ("transport", "Cycle Station", "Bicycle hire for twenty minutes", 350),
            ("transport", "Town Taxi", "Taxi from station to apartment", 1860),
            ("transport", "Park Square", "Two hours of street parking", 460),
            ("transport", "Roadside Fuel", "Petrol refill", 6240),
            ("utilities", "City Energy", "Monthly electricity bill", 7830),
            ("utilities", "Clear Water", "Household water service", 2840),
            ("utilities", "Warm Home", "Residential heating bill", 9650),
            ("utilities", "Mobile Line", "Monthly mobile phone plan", 1590),
            ("utilities", "City Waste", "Household waste collection", 1920),
            ("shopping", "Central Store", "Cotton towels and pillowcases", 3290),
            ("shopping", "Paper Corner", "Birthday card and wrapping paper", 640),
            ("shopping", "Footpath Shoes", "Everyday leather shoes", 7490),
            ("shopping", "Kitchen Shelf", "Saucepan and wooden utensils", 3860),
            ("shopping", "Book Nook", "Paperback novel", 1390),
            ("shopping", "Central Store", "Laundry detergent and dish soap", 1270),
            ("shopping", "Garden Shed", "Potting soil and herb seedlings", 1680),
            ("dining", "Harbor Fish", "Grilled fish lunch", 2240),
            ("transport", "City Transit", "Weekend bus ticket", 560)
        ]);
        AddTransactions(transactions, "C", 50,
        [
            ("shopping", "DeskSupply", "Adjustable monitor arm and laptop stand", 8790),
            ("shopping", "Screen Corner", "External display with USB-C connection", 24900),
            ("business-travel", "ConnectSpace", "Day pass with desk and meeting-room access", 2500),
            ("software", "CloudDesk", "Shared document editing subscription", 1290),
            ("software", "CallBridge", "Video conferencing plan with screen sharing", 1490),
            ("software", "Cinema Cloud", "Monthly films and television subscription", 1190),
            ("software", "Sound Stream", "Ad-free music listening plan", 1090),
            ("software", "Story Audio", "Monthly audiobook membership", 990),
            ("software", "Game Harbor", "Cloud gaming access subscription", 1690),
            ("travel", "Coast Rooms", "Three-night seaside guesthouse stay", 28500),
            ("travel", "Trail Tours", "Guided island walking excursion", 4500),
            ("shopping", "Central Store", "Cabin suitcase and luggage tags", 6890),
            ("travel", "City Museum", "Two adult exhibition admission tickets", 3200),
            ("business-travel", "Rail Connect", "Return train ticket for client meeting", 8600),
            ("business-travel", "Station Rooms", "Overnight stay before supplier workshop", 11200),
            ("business-travel", "Meeting Table", "Catering for customer presentation", 7450),
            ("business-travel", "Town Taxi", "Taxi to conference venue", 2180),
            ("shopping", "Fixing Shelf", "Replacement tap washer and sealant", 1240),
            ("utilities", "Home Repair", "Leaking kitchen pipe repair", 9500),
            ("shopping", "Color House", "Wall paint, roller and masking tape", 4780),
            ("shopping", "Trail Outfit", "Walking boots and wool hiking socks", 11900),
            ("shopping", "Cycle Station", "Bicycle brake pads and chain lubricant", 2370),
            ("shopping", "Pool Lane", "Swimming goggles and pool admission", 2490),
            ("shopping", "Move Studio", "Four group exercise classes", 4800)
        ]);
        AddTransactions(transactions, "A", 25,
        [
            ("shopping", "AudioCorner", "Headset with microphone", 5990),
            ("shopping", "Central Store", "Accessories and supplies", 2840),
            ("software", "Digital Hub", "Monthly subscription", 1290),
            ("shopping", "Central Store", "Card purchase", 3690),
            ("travel", "Station Rooms", "Stay near the station", 8900),
            ("transport", "Rail Connect", "Return train ticket", 4250),
            ("dining", "Meeting Table", "Lunch for three", 4860),
            ("shopping", "DeskSupply", "USB cable and adapter", 1890),
            ("utilities", "Broad Link", "Monthly internet access", 3490),
            ("shopping", "Book Nook", "Notebook and pens", 970),
            ("shopping", "Trail Outfit", "Lightweight jacket", 6490),
            ("software", "Cloud Vault", "Additional online storage", 299)
        ]);
        AddTransactions(transactions, "D", 25,
        [
            ("shopping", "ToyBarn", "Remote-control racing car", 3490),
            ("shopping", "Kitchen Shelf", "Water filter replacement cartridge", 1890),
            ("shopping", "Paper Corner", "Holiday greeting cards", 790),
            ("shopping", "Central Store", "Desktop photo frame", 1290),
            ("shopping", "Book Nook", "Novel about a business trip", 1490),
            ("shopping", "Pet Patch", "Dog training treats", 890),
            ("shopping", "Paper Corner", "Running shoe keyring", 690),
            ("shopping", "ToyBarn", "Model train starter set", 6990),
            ("shopping", "Garden Shed", "Conference pear tree sapling", 2490),
            ("shopping", "Garden Shed", "Plant support stand", 1590),
            ("shopping", "AudioCorner", "Replacement television remote", 1990),
            ("shopping", "Book Nook", "Travel photography book", 2490)
        ]);

        context.Transactions.AddRange(transactions.Where(transaction => !existingTransactionIds.Contains(transaction.Id)));
        await context.SaveChangesAsync(cancellationToken);
        await databaseTransaction.CommitAsync(cancellationToken);
    }

    private static void AddTransactions(
        List<Transaction> transactions,
        string group,
        int count,
        (string CategoryId, string Merchant, string Description, long MinorUnits)[] templates)
    {
        for (int index = 0; index < count; index++)
        {
            var template = templates[index % templates.Length];
            int cycle = index / templates.Length;
            DateOnly date = new DateOnly(2026, 1, 1).AddDays(index % templates.Length * 11 % 28).AddMonths(cycle);
            if (group == "C" && index < 3)
            {
                date = index switch
                {
                    0 => new DateOnly(2026, 3, 1),
                    1 => new DateOnly(2026, 3, 31),
                    _ => new DateOnly(2026, 4, 1)
                };
            }
            else
            {
                date = date.AddMonths(group switch { "C" => 3, "A" => 1, "D" => 3, _ => 0 });
            }

            // Preserve the independently asserted March groceries baseline.
            if (template.CategoryId == "groceries" && date.Month == 3)
            {
                date = date.AddMonths(1);
            }

            transactions.Add(CreateTransaction(
                FormattableString.Invariant($"TX-{group}{index + 1:000}"),
                date,
                template.CategoryId,
                template.Merchant,
                template.Description,
                template.MinorUnits));
        }
    }

    private static Transaction CreateTransaction(
        string id, DateOnly date, string categoryId, string merchant, string description, long minorUnits) => new()
    {
        Id = id,
        Date = date,
        CategoryId = categoryId,
        Merchant = merchant,
        Description = description,
        Amount = minorUnits / 100m,
        Currency = Currency
    };
}