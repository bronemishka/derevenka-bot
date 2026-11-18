using System.Net;
using System.Text.RegularExpressions;
using System.Text;
using DerevenkaBot.Core.Domain;

namespace DerevenkaBot.Core.Application;

public static class Texts
{
    public const string ChatDisabled = "💬 Чат сейчас недоступен. Попробуй позже.";
    public const string ChatTextOnly = "💬 Напиши вопрос текстом после /chat или ответь на моё сообщение.";
    public const string ChatFailure = "Не получилось ответить. Попробуй ещё раз: /chat и твой вопрос.";
    public const string ChatInterrupted = "Не удалось закончить ответ. Отправь вопрос ещё раз через /chat.";
    public static string Escape(string? text) => WebUtility.HtmlEncode(text ?? "");
    public static string Plain(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", ""));
    public static string Toast(string html)
    {
        var plain = Plain(html);
        if (plain.Length <= 180) return plain;
        var output = new StringBuilder();
        foreach (var rune in plain.EnumerateRunes())
        {
            if (output.Length + rune.Utf16SequenceLength > 177) break;
            output.Append(rune.ToString());
        }
        return output + "…";
    }
    public const string Generating = "🎨 <b>Создаю картинку…</b>\nПришлю сюда, когда будет готово.";
    public const string Queued = "⏳ <b>Картинка в очереди</b>\nПришлю результат в этот чат.";
    public const string Duplicate = "Этот запрос уже принят. Подожди, пожалуйста.";
    public const string Denied = "👋 <b>Нужен доступ</b>\nНажми <b>«Запросить доступ»</b> — владелец рассмотрит заявку.\n\nВ личке также можно написать /request_access. Свой ID: /ai_id.";
    public const string MissingImage = "🖼 <b>Какую картинку меняем?</b>\nПришли фото с подписью или ответь командой /edit на картинку.";
    public const string MissingPrompt = "💭 <b>Что нарисовать?</b>\nДобавь описание после /imagine или отправь фото с инструкцией.";
    public const string Reset = "🆕 <b>Можно начать заново</b>\nИстория разговора очищена. Следующая картинка начнётся с нуля. Старые картинки можно изменить, ответив на них.";
    public const string Busy = "⏳ <b>Предыдущая картинка ещё готовится</b>\nДождись результата, чтобы изменить его. Для отдельной картинки используй /imagine с новым описанием.";
    public const string QueueFull = "🚦 <b>Очередь заполнена</b>\nПопробуй отправить запрос немного позже.";
    public const string Unavailable = "🕰 <b>Картинка недоступна</b>\nВозможно, срок хранения истёк или она находится в другом чате. Пришли исходник заново.";
    public const string InvalidFile = "📎 <b>Не получилось прочитать файл</b>\nПришли один PNG, JPEG или WebP. Альбомы пока не поддерживаются. Для лучшего качества отправляй изображение как файл.";
    public const string Failure = "😕 <b>Не получилось создать картинку</b>\nПопробуй изменить описание. Статистика запросов доступна через /usage.";
    public const string Unknown = "📡 <b>Сервис не вернул ответ</b>\nЗапрос мог выполниться, поэтому автоматически повторять его не буду. Учёт расходов доступен через /usage.";
    public const string Ready = "✨ <b>Готово!</b>\nОтветь на картинку с новой инструкцией или нажми <b>«Продолжить»</b>.\n<i>«Оригинал» — файл без сжатия Telegram.</i>";
    public const string Delivered = "✅ <b>Картинка отправлена</b>\nОна в сообщении ниже.";
    public const string Publishing = "📤 <b>Картинка готова — отправляю…</b>";
    public const string OwnerRequired = "🔐 <b>Это команда владельца</b>\nВыдавать и отзывать доступ может только владелец. Сверь свой UserId из /ai_id с <code>ImageGeneration.OwnerUserId</code> в настройках и перезапусти бота после изменения.";
    public const string AccessSyntax = "🤝 <b>Кому выдать доступ?</b>\n\n<b>Другу:</b> <code>/ai_allow 123456789</code>\nИли отправь /ai_allow в ответ на сообщение друга.\n\n<b>Всей группе:</b> <code>/ai_allow here</code>\n<b>Отозвать:</b> <code>/ai_revoke 123456789</code>\n<b>Группа по ID, в личке:</b> <code>/ai_allow chat -100123</code>\n\nИспользуй числовой ID, не @username. Список разрешений: /ai_access.";
    public const string ReplyUserUnavailable = "👤 <b>Не вижу ID этого человека</b>\nОтветь на обычное сообщение друга либо укажи числовой UserId: <code>/ai_allow 123456789</code>. Друг может узнать ID командой /ai_id.";
    public const string PrivateAccess = "🔐 <b>Разрешения — в личке</b>\nОткрой личный чат со мной и отправь /ai_access.";
    public const string AlreadyHandled = "👌 Эта команда уже выполнена.";
    public const string PromptExpired = "⌛ <b>Это сообщение уже неактуально</b>\nОткрой меню через /start и выбери действие ещё раз.";
    public const string InputCancelled = "↩️ <b>Ввод отменён</b>\nВыбери «Чат» или «Картинки».";
    public const string UploadFirst = "📸 <b>Пришли фото</b>\nВ подписи напиши, что изменить. Можно отправить фото без подписи — тогда спрошу отдельно.\n\nОтмена: /cancel.";
    public const string UnexpectedId = "👤 <b>Похоже, это Telegram ID</b>\nЧтобы добавить друга, отправь <code>/ai_allow ID</code> или выбери «Управление доступом» → «Добавить друга».\n<i>Если это описание картинки, укажи его после /imagine.</i>";
    public const string RequestSent = "📨 <b>Заявка сохранена</b>\nПередам её владельцу. Как только он примет решение, сообщу здесь. Отправлять повторно не нужно.";
    public const string RequestAlreadySent = "⏳ <b>Твоя заявка уже на рассмотрении</b>\nПовторно отправлять её не нужно — сообщу, когда владелец ответит.";
    public const string RequestInPrivate = "💬 <b>Запроси доступ в личке</b>\nОткрой личный чат со мной и нажми «Запросить доступ» или отправь /request_access. Одобрение даст личный доступ тебе, а не всей группе.";
    public const string RequestsUnavailable = "🔧 <b>Заявки пока недоступны</b>\nВладелец ещё не настроен. Попроси администратора указать OwnerUserId.";
    public const string AlreadyAllowed = "✅ <b>У тебя уже есть доступ</b>\nОткрой /start и выбери «Чат» или «Картинки».";
    public const string RequestNotFound = "Эта заявка больше недоступна.";
    public const string RequestAlreadyReviewed = "По этой заявке уже принято решение. Старые кнопки не меняют доступ повторно.";
    public const string RequestApprovedToast = "✅ Заявка одобрена. Пользователь получит уведомление.";
    public const string RequestDeclinedToast = "❌ Заявка отклонена. Пользователь получит уведомление.";
    public static string AccessRequestCard(AccessRequest request)
    {
        var title = request.Status switch { AccessRequestStatus.Approved => "✅ Заявка одобрена", AccessRequestStatus.Declined => "❌ Заявка отклонена", _ => "📨 Запрос на доступ" };
        var name = Escape(string.IsNullOrWhiteSpace(request.DisplayName) ? "Пользователь" : request.DisplayName);
        var username = string.IsNullOrWhiteSpace(request.Username) ? "" : $"\n@{Escape(request.Username)}";
        return $"<b>{title}</b>\n\n👤 <a href=\"tg://user?id={request.Applicant.UserId}\">{name}</a>{username}\n🪪 ID: <code>{request.Applicant.UserId}</code>\n\n" +
            (request.Status == AccessRequestStatus.Pending ? "Дать этому человеку доступ к боту в личке и группах?" : "Решение сохранено. Проверить доступ: /ai_access.");
    }
    public static string AccessDecision(AccessRequest request) => request.Status == AccessRequestStatus.Approved
        ? "✅ <b>Доступ открыт</b>\nВыбери «Чат» или «Картинки» ниже."
        : "❌ <b>Твоя заявка отклонена</b>\nВладелец не выдал доступ по этой заявке. Если нужно, свяжись с ним лично.";
    public static string AccessInbox(IReadOnlyList<AccessRequest> requests) => "📥 <b>Заявки на доступ</b>\n\n" +
        (requests.Count == 0 ? "Новых заявок нет. ✨" :
            string.Join("\n", requests.Take(10).Select((x, i) => $"{i + 1}. {Escape(x.DisplayName)} · <code>{x.Applicant.UserId}</code>")) +
            "\n\nВыбери решение кнопками ниже." + (requests.Count > 10 ? $"\nПоказаны 10 из {requests.Count}. После обработки обнови список." : ""));
    public const string Help = """
        <h2>🏠 Главное меню · #деревенька</h2>
        <p>Выбери, что хочешь сделать.</p>
        <p><b>💬 Чат</b> — задать вопрос или поговорить.<br><b>🎨 Картинки</b> — создать картинку или изменить фото.</p>
        <details><summary>Как пользоваться</summary>
        <p><b>Чат:</b> нажми кнопку или напиши /chat. Ответь на сообщение бота своим вопросом. Дальше продолжай через «Ответить» на мои сообщения.</p>
        <p><b>Картинки:</b> выбери создание или изменение. Можно сразу написать /imagine и описание, /edit и изменения или прислать фото с подписью.</p>
        <p>Чтобы изменить готовую картинку, ответь на неё или нажми «Продолжить».</p>
        <p>/cancel — отменить ввод. /reset — начать разговор и картинки заново.</p>
        </details>
        <footer>/usage — статистика · /ai_id — твой ID</footer>
        """;
    public const string ImagesHelp = """
        <h2>🎨 Картинки</h2>
        <p><b>Создать картинку</b> — напиши, что нарисовать.<br><b>Изменить фото</b> — пришли фото или измени последнюю картинку.</p>
        <p>Для изменения готовой картинки можно ответить на неё и написать, что поменять.</p>
        """;
    public const string AdminHelp = """

        <details><summary>🔑 Управление доступом</summary>
        <p><code>/ai_allow 123456789</code> — дать другу доступ по ID. Или ответь /ai_allow на его сообщение.</p>
        <p><code>/ai_allow here</code> — разрешить эту группу.</p>
        <p><code>/ai_revoke 123456789</code> — отозвать разрешение друга.</p>
        <p>/ai_access — список разрешений в личке. Разрешение человека и группы независимы.</p>
        </details>
        """;
    public static string Privacy(int retentionDays) => $"""
        <h2>🗂 История и данные</h2>
        <ul><li>У каждого человека своя история в каждом чате.</li>
        <li>В группе можно изменить только картинку из этой группы.</li>
        <li>Ответ на чужую картинку не открывает личную переписку её автора.</li>
        <li>Файлы доступны {retentionDays} дней. Удаление сообщения Telegram не удаляет серверный файл.</li></ul>
        <p>Кнопка «Начать заново» очищает историю разговора и начинает следующую картинку с нуля. Старые картинки сохраняются.</p>
        <footer>/usage показывает примерные расходы на картинки. Текстовые ответы оплачиваются отдельно.</footer>
        """;
    public static string Identity(Actor actor) => $"🪪 <b>Твои Telegram ID</b>\n\n👤 User ID: <code>{actor.UserId}</code>\n💬 Chat ID: <code>{actor.ChatId}</code>\n\n<i>Нажми на число, чтобы скопировать, или используй кнопку ниже.</i>";
    public static string DraftSaved(int minutes) => $"📸 <b>Фото получено</b>\nНапиши, что изменить. Фото доступно <b>{minutes} мин.</b>\nНапример: <i>сделай фон зимним</i>.\n\nОтмена: /cancel.";
    public static string ReplyHint(Actor actor, bool requireReply = false)
    {
        var hint = requireReply || !actor.IsPrivate
            ? "Нажми «Ответить» на это сообщение и напиши текст."
            : "Напиши следующим сообщением.";
        return actor.IsPrivate ? hint : $"<a href=\"tg://user?id={actor.UserId}\">{hint}</a>";
    }
    public static string Prompt(string action, Actor actor) => (action switch
    {
        "chat" => "💬 <b>Напиши вопрос</b>\nМожно что-то спросить или просто поговорить. Дальше отвечай на мои сообщения.",
        "new" => "✨ <b>Опиши картинку</b>\nНапример: <i>дом у озера, акварель</i>.",
        "allow" => "🤝 <b>Кому дать доступ?</b>\nПерешли сообщение человека или напиши его ID. Узнать свой ID можно через /ai_id.",
        "revoke" => "🔐 <b>У кого убрать доступ?</b>\nПерешли сообщение человека или напиши его ID.",
        _ => "✏️ <b>Что изменить?</b>\nНапример: <i>сделай фон зимним, остальное оставь</i>."
    }) + "\n\n" + ReplyHint(actor, action == "chat") + "\nОтмена: /cancel.";
}
