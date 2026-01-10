using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Bcv.Shared;
using Supabase;
using System.Collections.Concurrent;

namespace Bcv.Api.Services
{
    public class TelegramBotService : BackgroundService
    {
        private readonly IConfiguration _config;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<TelegramBotService> _logger;
        private static readonly ConcurrentDictionary<long, string> _estadoUsuarios = new();

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
            botClient.StartReceiving(HandleUpdateAsync, HandlePollingErrorAsync, new ReceiverOptions(), stoppingToken);
            _logger.LogInformation("Bot Admin Corregido (Botones Optimizados) iniciado.");
        }

        async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken ct)
        {
            // 1. MANEJO DE CLICS (CALLBACKS) - CORREGIDO PARA DATOS LARGOS
            if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery != null)
            {
                await ManejarCallbacksSeguros(botClient, update.CallbackQuery, ct);
                return;
            }

            if (update.Message is not { Text: { } messageText } message) return;
            var chatId = message.Chat.Id;
            if (chatId.ToString() != _config["Telegram:ChatId"]) return;

            using var scope = _serviceProvider.CreateScope();
            var supabase = scope.ServiceProvider.GetRequiredService<Supabase.Client>();

            if (_estadoUsuarios.TryGetValue(chatId, out var estado) && messageText == "❌ Cancelar")
            {
                _estadoUsuarios.TryRemove(chatId, out _);
                await botClient.SendMessage(chatId, "Acción cancelada.", replyMarkup: ObtenerTecladoPrincipal(), cancellationToken: ct);
                return;
            }

            switch (messageText)
            {
                case "➕ Crear Cliente":
                    _estadoUsuarios[chatId] = "ESPERANDO_CLIENTE_NUEVO";
                    await botClient.SendMessage(chatId, "Formato: `Usuario, Key` o `Usuario, Key, 1`", replyMarkup: TecladoCancelar(), parseMode: ParseMode.Markdown, cancellationToken: ct);
                    break;

                case "📋 Listar Pendientes":
                    var resP = await supabase.From<ClienteApi>().Where(x => x.EstaAprobado == false).Get();
                    if (!resP.Models.Any()) await botClient.SendMessage(chatId, "✅ Todo al día.", cancellationToken: ct);
                    else await botClient.SendMessage(chatId, $"*Pendientes:*\n{string.Join("\n", resP.Models.Select(c => $"🔑 `{c.ApiKey}` (@{c.UsernameTelegram})"))}", parseMode: ParseMode.Markdown, cancellationToken: ct);
                    break;

                case "✅ Aprobar Cliente":
                    await MostrarMenuAprobar(botClient, chatId, supabase, ct);
                    break;

                case "🔄 Editar Estado":
                    await MostrarMenuEditar(botClient, chatId, supabase, ct);
                    break;

                case "🗑️ Eliminar Cliente":
                    await MostrarMenuEliminar(botClient, chatId, supabase, ct);
                    break;

                case "📊 Estado Sistema":
                    await botClient.SendMessage(chatId, "🚀 API Online", cancellationToken: ct);
                    break;

                case "/start":
                    await botClient.SendMessage(chatId, "🛡️ Panel Administrativo.", replyMarkup: ObtenerTecladoPrincipal(), cancellationToken: ct);
                    break;

                default:
                    if (_estadoUsuarios.TryGetValue(chatId, out var est) && est == "ESPERANDO_CLIENTE_NUEVO")
                        await EjecutarCreacionCliente(botClient, chatId, messageText, supabase, ct);
                    break;
            }
        }

        private async Task MostrarMenuEditar(ITelegramBotClient bot, long chatId, Supabase.Client db, CancellationToken ct)
        {
            var res = await db.From<ClienteApi>().Get();
            if (!res.Models.Any()) { await bot.SendMessage(chatId, "No hay clientes."); return; }

            var botones = res.Models.Select(c => {
                string icon = c.EstaAprobado ? "✅" : "🚫";
                // Visualmente mostramos todo, pero en el callback solo mandamos la Key (limitada si es necesario)
                return new[] { InlineKeyboardButton.WithCallbackData($"{icon} {c.UsernameTelegram} | {c.ApiKey}", $"tg_st:{c.ApiKey}") };
            }).ToArray();

            await bot.SendMessage(chatId, "Toca para cambiar estado:", replyMarkup: new InlineKeyboardMarkup(botones), cancellationToken: ct);
        }

        private async Task MostrarMenuEliminar(ITelegramBotClient bot, long cid, Supabase.Client db, CancellationToken ct)
        {
            var res = await db.From<ClienteApi>().Get();
            if (!res.Models.Any()) return;
            var btns = res.Models.Select(c => new[] { InlineKeyboardButton.WithCallbackData($"🗑️ {c.UsernameTelegram} | {c.ApiKey}", $"pre_del:{c.ApiKey}") }).ToArray();
            await bot.SendMessage(cid, "Selecciona para borrar:", replyMarkup: new InlineKeyboardMarkup(btns));
        }

        private async Task MostrarMenuAprobar(ITelegramBotClient bot, long cid, Supabase.Client db, CancellationToken ct)
        {
            var res = await db.From<ClienteApi>().Where(x => x.EstaAprobado == false).Get();
            if (!res.Models.Any()) { await bot.SendMessage(cid, "✅ Sin pendientes."); return; }
            var btns = res.Models.Select(c => new[] { InlineKeyboardButton.WithCallbackData($"✅ Aprobar: {c.UsernameTelegram}", $"ok_ap:{c.ApiKey}") }).ToArray();
            await bot.SendMessage(cid, "Aprobar acceso:", replyMarkup: new InlineKeyboardMarkup(btns));
        }

        // --- MANEJO DE CALLBACKS OPTIMIZADO PARA EVITAR EXCESO DE BYTES ---
        private async Task ManejarCallbacksSeguros(ITelegramBotClient bot, CallbackQuery query, CancellationToken ct)
        {
            var data = query.Data ?? "";
            var chatId = query.Message!.Chat.Id;
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Supabase.Client>();

            try
            {
                if (data.StartsWith("tg_st:")) // Toggle Status
                {
                    var key = data.Replace("tg_st:", "");
                    var actual = await db.From<ClienteApi>().Where(x => x.ApiKey == key).Get();
                    var cliente = actual.Models.FirstOrDefault();
                    if (cliente != null)
                    {
                        bool nuevo = !cliente.EstaAprobado;
                        await db.From<ClienteApi>().Where(x => x.ApiKey == key).Set(x => x.EstaAprobado, nuevo).Update();
                        await bot.AnswerCallbackQuery(query.Id, "Estado cambiado");
                        await bot.EditMessageText(chatId, query.Message.MessageId, $"🔄 Key: `{key}`\nEstado: {(nuevo ? "✅ ACTIVO" : "🚫 BLOQUEADO")}", parseMode: ParseMode.Markdown);
                    }
                }
                else if (data.StartsWith("ok_ap:")) // Aprobar
                {
                    var key = data.Replace("ok_ap:", "");
                    await db.From<ClienteApi>().Where(x => x.ApiKey == key).Set(x => x.EstaAprobado, true).Update();
                    await bot.EditMessageText(chatId, query.Message.MessageId, $"✅ Key `{key}` aprobada.");
                }
                else if (data.StartsWith("pre_del:")) // Pre-Eliminar
                {
                    var key = data.Replace("pre_del:", "");
                    var kb = new InlineKeyboardMarkup(new[] {
                        new[] { InlineKeyboardButton.WithCallbackData("🔥 CONFIRMAR", $"f_del:{key}") },
                        new[] { InlineKeyboardButton.WithCallbackData("🔙 Volver", "back") }
                    });
                    await bot.EditMessageText(chatId, query.Message.MessageId, $"⚠️ ¿Borrar permanentemente?\nKey: `{key}`", replyMarkup: kb, parseMode: ParseMode.Markdown);
                }
                else if (data.StartsWith("f_del:")) // Force Delete
                {
                    var key = data.Replace("f_del:", "");
                    await db.From<ClienteApi>().Where(x => x.ApiKey == key).Delete();
                    await bot.EditMessageText(chatId, query.Message.MessageId, "🗑️ Registro eliminado.");
                }
                else if (data == "back") await bot.DeleteMessage(chatId, query.Message.MessageId);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error en Callback: {msg}", ex.Message);
                await bot.AnswerCallbackQuery(query.Id, "Error al procesar acción.");
            }
        }

        private async Task EjecutarCreacionCliente(ITelegramBotClient bot, long cid, string txt, Supabase.Client db, CancellationToken ct)
        {
            var d = txt.Split(',');
            if (d.Length < 2) return;
            bool ap = d.Length >= 3 && d[2].Trim() == "1";
            await db.From<ClienteApi>().Insert(new ClienteApi { UsernameTelegram = d[0].Trim(), ApiKey = d[1].Trim(), EstaAprobado = ap });
            _estadoUsuarios.TryRemove(cid, out _);
            await bot.SendMessage(cid, "✅ Cliente creado.", replyMarkup: ObtenerTecladoPrincipal());
        }

        private ReplyKeyboardMarkup ObtenerTecladoPrincipal() => new(new[] {
            new[] { new KeyboardButton("➕ Crear Cliente"), new KeyboardButton("📋 Listar Pendientes") },
            new[] { new KeyboardButton("✅ Aprobar Cliente"), new KeyboardButton("🔄 Editar Estado") },
            new[] { new KeyboardButton("🗑️ Eliminar Cliente"), new KeyboardButton("📊 Estado Sistema") }
        })
        { ResizeKeyboard = true };

        private ReplyKeyboardMarkup TecladoCancelar() => new(new[] { new KeyboardButton("❌ Cancelar") }) { ResizeKeyboard = true };
        Task HandlePollingErrorAsync(ITelegramBotClient b, Exception ex, CancellationToken ct) => Task.CompletedTask;
    }
}