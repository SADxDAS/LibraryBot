# Анализ оптимизации AddBookToCatalogAsync через UPSERT

## Текущее состояние

```csharp
public static async Task<bool> AddBookToCatalogAsync(string title, string author, string genre, string exchangeStatus, int quantity = 1)
{
	using var db = new AppDbContext();
	db.Books.Add(new DbBook { ... });
	await db.SaveChangesAsync();
	return true;
}
```

**Проблемы:**
1. Может создать дубли по названию (если не проверить перед вызовом)
2. Каждое добавление — отдельная операция в БД
3. В AdminStateHandler уже есть проверка похожих книг и логика выбора "увеличить кількість" — это partial upsert

## Рекомендуемая оптимизация

### Вариант 1: Upsert через SQL (PostgreSQL, быстрее)
```sql
INSERT INTO "Books" (title, author, genre, exchange_status, available_count, total_count)
VALUES (@title, @author, @genre, @exchangeStatus, @quantity, @quantity)
ON CONFLICT ("Title", "Author") DO UPDATE SET
  total_count = total_count + @quantity,
  available_count = available_count + @quantity
RETURNING id;
```

### Вариант 2: Логика на C# (более контролируемо)
```csharp
public static async Task<int> UpsertBookAsync(string title, string author, string genre, string exchangeStatus, int quantity = 1)
{
	using var db = new AppDbContext();
	var existing = await db.Books.FirstOrDefaultAsync(b => 
		b.Title.ToLower() == title.ToLower() && 
		b.Author.ToLower() == author.ToLower());

	if (existing != null)
	{
		// Update existing
		existing.TotalCount += quantity;
		existing.AvailableCount += quantity;
		existing.ExchangeStatus = exchangeStatus;
		await db.SaveChangesAsync();
		return existing.Id;
	}
	else
	{
		// Add new
		var book = new DbBook { Title = title, Author = author, ... };
		db.Books.Add(book);
		await db.SaveChangesAsync();
		return book.Id;
	}
}
```

## Где это поможет

1. **AddBookToCatalogAsync** → переменовать на UpsertBookAsync
2. **AdminStateHandler.WaitingForAddBookDuplicateCheck** → выбор "+1 {title}" → вместо отдельного UpdateBookInCatalogAsync, использовать UpsertBookAsync
3. **Конец wizard-а** → при добавлении новой книги использовать UpsertBookAsync

## Быстрота

- SQL ON CONFLICT: **быстрее** (одна операция в БД вместо SELECT + UPDATE/INSERT)
- C# логика: **медленнее** на 1 SELECT, но **понятнее** и **безопаснее** для отката при ошибке

**Рекомендация:** использовать Вариант 1 (SQL) для максимальной производительности, но обработать исключения.
