"""Add Kauri's own strings to the string catalogs (idempotent).

usage: python scripts/kauri_strings.py   (from macos/)

Each key gets English and Russian values; other languages fall back to
English until someone translates them.
"""
import json
import pathlib

ROOT = pathlib.Path(__file__).resolve().parent.parent

SETTINGS = {
    # About
    "About": ("About", "О программе"),
    "Releases…": ("Releases…", "Выпуски…"),
    "Based on:": ("Based on:", "Основано на:"),
    "basedOnClipyDescription": (
        "Kauri Clip is a fork of Clipy, the clipboard extension app for macOS, under the MIT license.",
        "Kauri Clip — форк Clipy, расширения буфера обмена для macOS, по лицензии MIT.",
    ),
    "Clipy on GitHub…": ("Clipy on GitHub…", "Clipy на GitHub…"),
    # Sync
    "Sync": ("Sync", "Синхронизация"),
    "Server:": ("Server:", "Сервер:"),
    "Status:": ("Status:", "Состояние:"),
    "Sync is off": ("Sync is off", "Синхронизация выключена"),
    "Connecting…": ("Connecting…", "Подключение…"),
    "Up to date": ("Up to date", "Всё синхронизировано"),
    "Syncing…": ("Syncing…", "Синхронизация…"),
    "Offline, will retry": ("Offline, will retry", "Нет связи, повторим позже"),
    "Start Sync…": ("Start Sync…", "Включить синхронизацию…"),
    "Join with a Pairing Code…": ("Join with a Pairing Code…", "Подключить по коду…"),
    "Join with the Sync Code…": ("Join with the Sync Code…", "Подключить по 24 словам…"),
    "Turn Off Sync…": ("Turn Off Sync…", "Выключить синхронизацию…"),
    "What syncs:": ("What syncs:", "Что синхронизировать:"),
    "Snippets and folders": ("Snippets and folders", "Сниппеты и папки"),
    "syncHistoryToggle": ("Clipboard history", "История буфера обмена"),
    "syncHistoryDescription": (
        "Text only, up to 500 items. Confidential content and excluded apps are never synced.",
        "Только текст, до 500 записей. Конфиденциальное содержимое и исключённые приложения не синхронизируются.",
    ),
    "Devices:": ("Devices:", "Устройства:"),
    "This Mac": ("This Mac", "Этот Mac"),
    "removeDeviceButton": ("Remove…", "Удалить…"),
    "Add a Device…": ("Add a Device…", "Добавить устройство…"),
    "Show Sync Code…": ("Show Sync Code…", "Показать 24 слова…"),
    "syncDescription": (
        "Everything is encrypted on this Mac before it leaves. The server sees neither your snippets nor their titles.",
        "Всё шифруется на этом Mac до отправки. Сервер не видит ни сниппеты, ни их названия.",
    ),
    "Not post-quantum": ("Not post-quantum", "Без постквантовой защиты"),
}

LOCALIZABLE = {
    "Sync Code": ("Sync Code", "Код синхронизации"),
    "syncCodeMessage": (
        "These 24 words open your synced data. Write them down and keep them safe: anyone who has them can read your snippets. You need them if you lose all your devices.",
        "Эти 24 слова открывают синхронизированные данные. Запишите их и храните надёжно: у кого они есть, тот может прочитать сниппеты. Они понадобятся, если вы потеряете все устройства.",
    ),
    "Copy": ("Copy", "Скопировать"),
    "OK": ("OK", "OK"),
    "Join with the Sync Code…": ("Join with the Sync Code…", "Подключить по 24 словам…"),
    "Join with a Pairing Code…": ("Join with a Pairing Code…", "Подключить по коду…"),
    "Add a Device…": ("Add a Device…", "Добавить устройство…"),
    "Turn Off Sync…": ("Turn Off Sync…", "Выключить синхронизацию…"),
    "Done": ("Done", "Готово"),
    "Cancel": ("Cancel", "Отмена"),
    "Join": ("Join", "Подключить"),
    "Allow": ("Allow", "Разрешить"),
    "Deny": ("Deny", "Запретить"),
    "Remove": ("Remove", "Удалить"),
    "Turn Off": ("Turn Off", "Выключить"),
    "Start": ("Start", "Включить"),
    "Sync server URL": ("Sync server URL", "Адрес сервера синхронизации"),
    "startSyncMessage": (
        "Kauri Clip will create a new sync chain on this server and show you its 24-word sync code.",
        "Kauri Clip создаст новую цепочку синхронизации на этом сервере и покажет 24 слова для её восстановления.",
    ),
    "Enter the code shown on your other device": ("Enter the code shown on your other device", "Введите код с другого устройства"),
    "Enter the 24 words": ("Enter the 24 words", "Введите 24 слова"),
    "Pairing Code": ("Pairing Code", "Код подключения"),
    "pairingCodeMessage": (
        "On the new device, choose Join with a Pairing Code and enter this code. It works for 5 minutes.",
        "На новом устройстве выберите «Подключить по коду» и введите этот код. Он действует 5 минут.",
    ),
    "Waiting for the other device…": ("Waiting for the other device…", "Ждём другое устройство…"),
    "allowDeviceTitle %@": ("Allow “%@” to sync?", "Разрешить синхронизацию для «%@»?"),
    "allowDeviceMessage %@": (
        "Check that the other device shows the number %@.",
        "Проверьте, что другое устройство показывает число %@.",
    ),
    "joinNumberMessage %@": (
        "Check that your other device shows the number %@, then allow this Mac there.",
        "Проверьте, что другое устройство показывает число %@, и разрешите там этот Mac.",
    ),
    "removeDeviceTitle %@": ("Remove “%@”?", "Удалить «%@»?"),
    "removeDeviceMessage": (
        "The device stops syncing at once. Your other devices move to a new sync code, which Kauri Clip will show you.",
        "Устройство сразу перестанет синхронизироваться. Остальные устройства перейдут на новый код синхронизации, Kauri Clip покажет его.",
    ),
    "turnOffSyncMessage": (
        "This Mac leaves the sync chain. Your snippets stay on this Mac and on your other devices.",
        "Этот Mac выйдет из цепочки синхронизации. Сниппеты останутся и на этом Mac, и на других устройствах.",
    ),
    "This Mac was removed from sync.": ("This Mac was removed from sync.", "Этот Mac удалён из синхронизации."),
    "Sync failed": ("Sync failed", "Ошибка синхронизации"),
    "The server must be an https:// address.": ("The server must be an https:// address.", "Адрес сервера должен начинаться с https://."),
    "The code is not valid.": ("The code is not valid.", "Неверный код."),
    "The words are not a valid sync code.": ("The words are not a valid sync code.", "Слова не образуют код синхронизации."),
    "The pairing was not confirmed. Try a new code.": ("The pairing was not confirmed. Try a new code.", "Подключение не подтверждено. Попробуйте новый код."),
}


# Keys an earlier version of this script added.
STALE = ["Clipboard history", "Remove…"]


def add(path, table):
    p = ROOT / path
    d = json.loads(p.read_text(encoding="utf-8"))
    for key in STALE:
        if key in d["strings"] and key not in table and "ru" in d["strings"][key].get("localizations", {}):
            del d["strings"][key]
    for key, (en, ru) in table.items():
        entry = d["strings"].setdefault(key, {})
        entry["extractionState"] = "manual"
        locs = entry.setdefault("localizations", {})
        locs["en"] = {"stringUnit": {"state": "translated", "value": en}}
        locs["ru"] = {"stringUnit": {"state": "translated", "value": ru}}
    p.write_text(json.dumps(d, ensure_ascii=False, indent=2, separators=(",", " : ")) + "\n", encoding="utf-8", newline="\n")
    print("strings:", path, len(table))


add("Clipy/Resources/Settings.xcstrings", SETTINGS)
add("Clipy/Resources/Localizable.xcstrings", LOCALIZABLE)

# Russian is a known region of the project.
pbx = ROOT / "Clipy.xcodeproj/project.pbxproj"
s = pbx.read_text(encoding="utf-8")
if "\t\t\t\tru,\n" not in s:
    s = s.replace("\t\t\t\tuk,\n\t\t\t);", "\t\t\t\tuk,\n\t\t\t\tru,\n\t\t\t);", 1)
    pbx.write_text(s, encoding="utf-8", newline="\n")
    print("added ru to knownRegions")
