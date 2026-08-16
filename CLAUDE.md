# COSLY_HIGH_PRICE_BOT

App de consola en .NET 9 que detecta "pumps" en Binance: consulta el ticker de 24 horas,
se queda con los pares `*USDT` que subieron más de un umbral configurable (100% por
defecto) y envía un único aviso formateado a Telegram.

Si ninguna moneda supera el umbral, **no envía nada** — sólo lo informa por consola.

## Log

Todo lo que sale por consola se escribe además en `Logs\pumps-<yyyy-MM-dd>.log`, junto
al ejecutable: un archivo por día, con `yyyy-MM-dd HH:mm:ss [NIVEL] mensaje` por línea.

Eventos que quedan registrados: inicio y fin de la ejecución (con el código de salida),
cada símbolo avisado por Telegram, cada símbolo que baja del umbral y se borra del JSON,
los pares descartados por estar suspendidos, y cualquier excepción.

Si la carpeta no se puede escribir, el logueo a archivo se apaga y el programa sigue por
consola: no poder loguear nunca puede impedir que llegue el aviso del pump.

Al arrancar se borran los logs más viejos que `Logging:RetentionDays` (30 por defecto,
`0` conserva todos). La antigüedad sale de **la fecha del nombre del archivo**, no de su
fecha de modificación, para que copiar la carpeta no los rejuvenezca. Los archivos que no
siguen el patrón `pumps-<yyyy-MM-dd>.log` no se tocan.

## Aviso único por símbolo

Cada símbolo avisado se guarda en `notified-symbols.json` (un array JSON, junto al
ejecutable) y no se vuelve a avisar mientras siga por encima del umbral. Cuando baja,
se borra del archivo, así que si más adelante repite el pump vuelve a avisar.

El ciclo de cada corrida:

1. Leer el archivo (si no existe, se arranca con la lista vacía).
2. Calcular las monedas que hoy superan el umbral y están operables.
3. Quitar del archivo las que ya no están en esa lista.
4. Avisar sólo de las que no estaban en el archivo.
5. Guardar el archivo con las monedas del punto 2.

El guardado ocurre **después** del envío: si Telegram falla, la corrida termina en `1`
sin registrar nada y la próxima reintenta. Un archivo corrupto no rompe el programa —
se avisa por consola, se ignora y se reescribe (el costo es un posible aviso repetido).

## Ejecutar

```bash
dotnet run --project src/CoslyHighPriceBot
```

Ejecución única: corre, avisa y termina. Códigos de salida: `0` correcto (haya o no
coincidencias), `1` error.

## Publicar para el Programador de tareas

```bash
publish.cmd
```

Deja `publish\CoslyHighPriceBot.exe` (un solo archivo, ~575 KB) junto a su
`appsettings.json`. Es *framework-dependent*: necesita el runtime .NET 9 en la máquina.
Para que no dependa del runtime, agregar `--self-contained true` al script (pasa a ~70 MB).

El programa lee la configuración desde `AppContext.BaseDirectory`, así que **no importa
el directorio de trabajo** con el que lo lance el Programador de tareas.

Ojo: `publish\appsettings.json` es una copia. Si se cambia el umbral ahí, hay que
cambiarlo también en `src\CoslyHighPriceBot\appsettings.json` o el próximo `publish.cmd`
lo pisa.

## Configuración

Todo valor ajustable vive en `src/CoslyHighPriceBot/appsettings.json`:

| Clave | Descripción |
| --- | --- |
| `Binance:Ticker24hUrl` | Endpoint del ticker 24h. Sin query string devuelve todos los símbolos. |
| `Binance:RollingTickerUrl` | Ticker de ventana móvil; de acá salen los % de 4h y 1h. |
| `Binance:ExchangeInfoUrl` | Estado de cada símbolo (TRADING / BREAK / HALT). |
| `Binance:QuoteAsset` | Moneda de cotización a filtrar (sufijo del símbolo). |
| `Binance:ExtraWindows` | Ventanas cortas a mostrar además de las 24h, en orden. Ej: `["4h", "1h"]`. |
| `Binance:OnlyTradingSymbols` | Descarta los pares suspendidos (ver más abajo). |
| `Filter:MinChangePercent` | Suba mínima en 24h, en %, para entrar en el aviso. |
| `State:NotifiedSymbolsFile` | Archivo con los símbolos ya avisados. Relativo = junto al ejecutable. |
| `Logging:RetentionDays` | Días de logs a conservar. `0` = no borrar ninguno. |
| `Telegram:ApiBaseUrl` | Base de la Bot API. |
| `Telegram:BotToken` | Token del bot. **Secreto.** |
| `Telegram:ChatId` | Chat destino. Privado = ID positivo; grupo/supergrupo = **negativo**. |

`appsettings.json` está en `.gitignore` porque contiene el token.
`appsettings.example.json` es la plantilla sin credenciales y sí se versiona.

Cualquier clave se puede pisar con una **variable de entorno** usando doble guión bajo
como separador: `Telegram__BotToken`, `Filter__MinChangePercent`,
`State__NotifiedSymbolsFile`. Se leen después del JSON, así que tienen prioridad. Es la
forma de pasar el token en la nube sin escribirlo en ningún archivo.

## Ejecución en la nube (GitHub Actions)

[`.github/workflows/pump-alert.yml`](.github/workflows/pump-alert.yml) corre el bot cada
15 minutos sin depender de ninguna PC encendida.

Requiere dos secrets en el repo (Settings → Secrets and variables → Actions):
`TELEGRAM_BOT_TOKEN` y `TELEGRAM_CHAT_ID`.

El estado vive en `state/notified-symbols.json`, **versionado a propósito**: es la única
forma de que la memoria del bot sobreviva entre corridas, porque el runner es efímero.
El workflow lo commitea al final de cada ejecución, y sólo si el envío salió bien.

Es gratis **si el repo es público**: cada corrida gasta un mínimo de 1 minuto facturable
y 2.880 corridas al mes superan los 2.000 minutos del plan gratuito para repos privados.

GitHub retrasa los cron programados cuando hay carga, así que el intervalo real puede
ser bastante mayor a 15 minutos.

## Estructura

```
src/CoslyHighPriceBot/
├─ Program.cs                    orquestación: config → fetch → filtro → aviso
├─ Configuration/AppSettings.cs  POCOs de appsettings.json + validación
├─ Models/Ticker24h.cs           DTOs de Binance (todo string) + records Coin y WindowChange
└─ Services/
   ├─ BinanceClient.cs           ticker 24h, ventanas móviles y estado de los símbolos
   ├─ CoinFilter.cs              filtrado por quote asset y % , ordenado desc
   ├─ MessageFormatter.cs        texto HTML del mensaje, partido si supera 4096 chars
   ├─ TelegramNotifier.cs        POST a sendMessage
   ├─ NotifiedSymbolStore.cs     lee/escribe notified-symbols.json
   └─ AppLog.cs                  consola + archivo diario en Logs/
```

Llamadas a Binance por ejecución: **1** si ninguna moneda supera el umbral (el caso
habitual). Si alguna lo supera, se suma 1 de `exchangeInfo`; y sólo si además hay
monedas nuevas para avisar, 1 por cada ventana de `ExtraWindows` (con todos los
símbolos juntos en el parámetro `symbols`).

Sin DI ni Generic Host: es un programa de un solo disparo y no lo justifica.
`global.json` fija el SDK 9.0.317 porque la máquina tiene un preview de .NET 10 por defecto.

## Detalles a tener en cuenta

- **`data-api.binance.vision`, no `api.binance.com`**: el dominio principal responde
  `451 Unavailable For Legal Reasons` desde IPs de datacenters de EE.UU., que es donde
  corren los runners de GitHub Actions. `data-api.binance.vision` es el endpoint público
  de datos de mercado (sólo lectura, sin API key) y sirve los tres endpoints que usamos.
- Binance devuelve **todos los campos numéricos como string**; el parseo usa
  `CultureInfo.InvariantCulture` (ver `CoinFilter`).
- **Pares suspendidos**: los símbolos en estado `BREAK` o `HALT` conservan sus
  estadísticas de 24h congeladas, así que aparecen como pumps enormes que en realidad
  no se pueden operar. En una prueba real, 4 de 9 monedas sobre el umbral estaban en
  `BREAK`. Por eso `OnlyTradingSymbols` viene en `true`.
- **Cuidado con los defaults de colecciones en la configuración**: el binder de
  `Microsoft.Extensions.Configuration` hace `Add()` sobre la lista que ya tiene la
  propiedad, no la reemplaza. `ExtraWindows` arranca en `[]` por eso; si se le pone
  `["4h", "1h"]` como valor por defecto, termina con los cuatro elementos duplicados.
- Si Binance no devuelve un símbolo en la respuesta de una ventana, se muestra `0%` y
  se deja un `AVISO` en consola: sin esa traza es indistinguible de "no se movió".
- **Enviar a un grupo**: no requiere cambios de código, sólo poner el ID del grupo en
  `Telegram:ChatId`. Para averiguarlo: agregar el bot al grupo, escribir `/start@<bot>`
  ahí (con el modo privacidad activado el bot sólo ve mensajes que empiezan con `/` o
  que lo mencionan) y leer `message.chat.id` de
  `https://api.telegram.org/bot<TOKEN>/getUpdates`. Si un grupo común se convierte en
  supergrupo, el ID cambia y hay que actualizarlo.
- El mensaje usa `parse_mode: HTML`, así que todo texto dinámico pasa por el escapado
  de `&`, `<`, `>` en `MessageFormatter.Escape`.
- El token va en la URL de Telegram: no debe aparecer nunca en logs ni en excepciones.
- Los precios van de decenas de miles a 0.00000001, por eso `FormatPrice` cambia de
  formato según la magnitud en lugar de usar un número fijo de decimales.
