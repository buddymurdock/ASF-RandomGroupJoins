# ASF-RandomGroupJoins

Плагин для **[ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm)**, который постепенно и вразнобой вступает ботами в Steam-группы из заданного вами пула — чтобы профили ботов не были пустыми и больше походили на аккаунты живых людей.

Каждому боту при первом обращении назначается случайная цель — сколько групп из пула он должен посетить, в диапазоне `[MinGroups; MaxGroups]` (ограничена размером самого пула). Пока бот не набрал цель, плагин раз в `DelayBetweenJoins` секунд выбирает случайного бота, которому есть куда вступать, и случайную ещё не тронутую им группу из пула, и отправляет заявку на вступление. За один тик обрабатывается не более одного вступления на весь инстанс ASF — это и есть задержка между вступлениями, она же защита от одновременного набега всех ботов на одну группу.

## Установка

1. Скачайте архив плагина из [Releases](../../releases) и распакуйте в папку `plugins` рядом с ASF (создайте подпапку с именем плагина).
2. Перезапустите ASF.

## Конфигурация

Настройки задаются **глобально**, в `ASF.json`, как дополнительные (нераспознанные ASF) свойства верхнего уровня:

```json
{
	"RandomGroupJoinsEnabled": true,
	"RandomGroupJoinsUseBundledGroups": true,
	"RandomGroupJoinsGroupIDs": [103582791429521408, 103582791435534462],
	"RandomGroupJoinsMinGroups": 1,
	"RandomGroupJoinsMaxGroups": 3,
	"RandomGroupJoinsDelayBetweenJoins": 300
}
```

| Свойство | Тип | По умолчанию | Описание |
| --- | --- | --- | --- |
| `RandomGroupJoinsEnabled` | `bool` | `false` | Включает/выключает плагин. |
| `RandomGroupJoinsGroupIDs` | `ulong[]` (или строки с числом) | `[]` | Пул Steam-групп (64-битные SteamID, тип "clan") из `ASF.json`. Складывается с пулом из `groups.json`, если он подключён (см. ниже). |
| `RandomGroupJoinsUseBundledGroups` | `bool` | `false` | Подключает пул групп из файла [`RandomGroupJoins/groups.json`](RandomGroupJoins/groups.json), который лежит прямо в этом репозитории рядом с исходником плагина и распространяется вместе со сборкой. Итоговый пул — объединение `RandomGroupJoinsGroupIDs` и `groups.json` (если включено), без дублей. |
| `RandomGroupJoinsMinGroups` | `byte` (0-255) | `1` | Нижняя граница случайной цели — в скольких группах из пула должен состоять бот. |
| `RandomGroupJoinsMaxGroups` | `byte` (0-255) | `3` | Верхняя граница случайной цели. Фактическая цель дополнительно ограничена размером пула. |
| `RandomGroupJoinsDelayBetweenJoins` | `ushort`, секунды | `300` | Пауза между вступлениями (одно вступление за тик на весь инстанс ASF). |

Если ни один из источников не дал ни одной группы, плагин один раз пишет предупреждение в лог и ничего не делает. Если `MinGroups` больше `MaxGroups`, значения меняются местами автоматически. Как узнать SteamID64 группы для `RandomGroupJoinsGroupIDs` — откройте страницу группы, посмотрите её ID (например через [steamdb.info](https://steamdb.info/) или `steamid.io`) — плагин ожидает именно 64-битный clan SteamID, а не короткое числовое `groupid` со старого API и не vanity-имя.

### groups.json

[`RandomGroupJoins/groups.json`](RandomGroupJoins/groups.json) — заранее собранный и провалидированный (через `memberslistxml` Steam API) пул из 45 крупных публичных Steam-групп (trading, киберспорт, игровые сообщества и т.п.), поддерживается прямо в этом репозитории:

```json
[
	{ "id": 103582791434277245, "name": "Steam Trading Cards Group", "url": "tradingcards" },
	...
]
```

Чтобы добавить свои группы — допишите объекты `{ "id", "name", "url" }` в этот файл (обязателен только `id`, `name`/`url` для читаемости) и включите `RandomGroupJoinsUseBundledGroups`. Файл собирается вместе с плагином и после релиза лежит рядом с DLL — редактировать его в уже установленном плагине можно и без пересборки, ASF читает его заново при каждом старте.

## Сборка

Проект использует **[ASF-PluginTemplate](https://github.com/JustArchiNET/ASF-PluginTemplate)** и собирается вместе с исходниками ASF, подключёнными как git submodule:

```sh
git clone --recurse-submodules https://github.com/buddymurdock/ASF-RandomGroupJoins.git
cd ASF-RandomGroupJoins
dotnet build -c Release
```

Если репозиторий уже склонирован без `--recurse-submodules`, подтяните submodule отдельно:

```sh
git submodule update --init --recursive
```

## Лицензия

Apache-2.0, см. [LICENSE.txt](LICENSE.txt).
