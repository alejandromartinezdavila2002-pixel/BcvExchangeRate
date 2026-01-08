using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Bcv.Shared;
using Supabase;

namespace Bcv.Api.Services
{
    public class TelegramBotService : BackgroundService
    {
        private readonly IConfiguration _config;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<TelegramBotService> _logger;

        public TelegramBotService(IConfiguration config, IServiceProvider serviceProvider, ILogger<TelegramBotService> logger)
        {
            _config = config;
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var token = _config["Telegram:Token"];
            if (string.IsNullOrEmpty(token)) return;

            var botClient = new TelegramBotClient(token);
            var receiverOptions = new ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() };

            botClient.StartReceiving(HandleUpdateAsync, HandlePollingErrorAsync, receiverOptions, stoppingToken);
            _logger.LogInformation("Bot de Administración iniciado en la API.");
        }

        async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken ct)
        {
            if (update.Message is not { Text: { } messageText } message) return;
            var chatId = message.Chat.Id.ToString();

            // Solo TÚ puedes administrar (usa tu ChatId configurado)
            if (chatId != _config["Telegram:ChatId"]) return;

            using var scope = _serviceProvider.CreateScope();
            var supabase = scope.ServiceProvider.GetRequiredService<Supabase.Client>();

            if (messageText.StartsWith("/aprobar"))
            {
                var key = messageText.Replace("/aprobar", "").Trim();
                await supabase.From<ClienteApi>().Where(x => x.ApiKey == key)
                    .Set(x => x.EstaAprobado, true).Update();

                await botClient.SendMessage(chatId, $"✅ Cliente con Key {key} aprobado.", cancellationToken: ct);
            }
            // Puedes agregar /bloquear, /listar, etc.
        }

        Task HandlePollingErrorAsync(ITelegramBotClient b, Exception ex, CancellationToken ct) => Task.CompletedTask;
    }
}