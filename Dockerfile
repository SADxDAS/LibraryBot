# Етап збірки
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Копіюємо файл проєкту і відновлюємо залежності
COPY ["LibraryBot.csproj", "./"]
RUN dotnet restore "LibraryBot.csproj"

# Копіюємо весь інший код і збираємо
COPY . .
RUN dotnet publish "LibraryBot.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Етап виконання (легкий образ для запуску)
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Запуск бота
ENTRYPOINT ["dotnet", "LibraryBot.dll"]