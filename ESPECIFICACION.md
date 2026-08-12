# WebServiceAlerter — Especificación

**Estado:** borrador para revisión
**Fecha:** 2026-08-11
**Base:** variante de ResourceAlerter (`D:\Works_VS\ResourceAlerter`)

---

## 1. Objetivo

Monitorear disponibilidad y latencia de webservices HTTP/SOAP desde el equipo del cliente, para
que el usuario pueda responder por sí mismo la pregunta **"¿el problema es el sistema de
facturación o es ARCA?"** antes de levantar el teléfono.

Caso de uso primario: caídas del webservice de facturación electrónica de ARCA, que hoy generan
una avalancha de reportes de todos los clientes en simultáneo. El programa queda abierto también
para cualquier otro servicio (bancos, pasarelas de pago, APIs propias).

### No-objetivos (decididos explícitamente)

| Fuera de alcance | Razón |
|---|---|
| Resumen diario por mail | El evento *es* la noticia; no hay tendencia que reportar. |
| Autenticación con certificados (WSAA / token+sign) | Agrega manejo de credenciales del cliente y riesgo de bloqueo por parte de ARCA. Todos los chequeos son anónimos. |
| Alertas centralizadas hacia SDigitales | Decidido: las alertas van **solo al cliente**. Cada instalación es autónoma. |
| Reintento/reenvío de comprobantes | Es responsabilidad del sistema de facturación, no del monitor. |

---

## 2. Decisiones de arquitectura

| Decisión | Valor |
|---|---|
| Despliegue | **Servicio Windows + Viewer** (mismo patrón que ResourceAlerter), **una instalación por cliente** |
| Destinatario de alertas | **Solo el cliente**, lista de mails editable por él |
| Canales | **Solo SMTP.** Discord queda afuera (ningún cliente lo usa) |
| Cuenta SMTP | **Fija, de un dominio de SDigitales**, no editable por el cliente — ver §7.1 |
| Configuración | **Dos capas**: `appsettings.json` (SDigitales, se sobrescribe al actualizar) + `usersettings.json` (cliente, nunca se toca) |
| Recarga de configuración | **En caliente, sin reiniciar el servicio** (el cliente no tiene elevación) |
| Proxy | **Fuera de alcance** — descartado explícitamente |
| Sin internet | **El servicio no hace nada**: no alerta, no abre incidente, no cuenta contra el uptime. Es un problema local y el usuario ya lo sabe |
| Profundidad de chequeo | **HTTP + WSDL + SOAP Dummy** desde la v1 |
| Framework | .NET 8, `Host.CreateApplicationBuilder` + `AddWindowsService` |
| Persistencia | SQLite en `%ProgramData%\WebServiceAlerter\` (WAL, lectura sin elevación) |

### Reuso desde ResourceAlerter

Se copia y se adapta, **no** se referencia como librería (proyectos independientes, versionado
independiente, instaladores independientes).

| Componente | Reuso |
|---|---|
| `Alerting/` (`IAlertSender`, `SmtpAlertSender`) | Tal cual, cambiando textos. Se conserva la interfaz `IAlertSender` y `CompositeAlertSender` (costo cero, ya escritos) para poder sumar un canal después; **`DiscordAlertSender` no se incluye en la v1** |
| `Alerting/AlertStateTracker` | **Reescrito** como `EndpointTracker`. La idea original era portarlo casi tal cual, pero entre el conteo de intentos, los estados nuevos (degradado, sin conexión, mantenimiento) y la compuerta del canario quedaba menos código escribiéndolo de cero que adaptándolo. El flujo conceptual —blip, confirmación, recordatorio, recuperación— es el mismo |
| `Data/DataRecorder` | Esquema extendido (ver §8); `PruneOrphanedSeries` sirve tal cual para URLs borradas de la config |
| `Logging/` (FileLogger + rotación) | Tal cual |
| `Localization/Strings` | Misma mecánica ES/EN, textos nuevos |
| `Viewer/` (shell, `ConfigStore`, `SettingsForm`, `ServiceRestarter`) | Shell y patrón sí; pantallas nuevas |
| `Monitors/` | **No se reusa** — se reemplaza por `Probes/` |
| `Reporting/DailySummaryService` | **Se descarta** |

**Diferencia conceptual clave con ResourceAlerter:** ahí los sujetos son fijos y conocidos en
compilación (CPU, disco, +12V); acá son N URLs definidas por configuración. Todo lo que asuma un
set fijo de monitores debe volverse data-driven.

---

## 3. Configuración — dos capas

Separar por **dueño**, no por máquina. Resuelve dos problemas a la vez: que una actualización
pueda corregir las URLs de ARCA sin pisar lo que el cliente editó, y que el cliente pueda editar
lo suyo sin tocar (ni ver) las credenciales SMTP.

| Archivo | Ubicación | Dueño | En una actualización |
|---|---|---|---|
| `appsettings.json` | Directorio de instalación | SDigitales | **Se sobrescribe** |
| `usersettings.json` | `%ProgramData%\WebServiceAlerter\` | El cliente (vía Viewer) | **Nunca se toca** |

Orden de carga: `appsettings.json` → `usersettings.json` (gana el segundo, key por key).
Esto reemplaza el mecanismo de ResourceAlerter, donde el instalador nunca toca `appsettings.json`
([Program.cs:24-34](../ResourceAlerter/src/ResourceAlerter/Program.cs)) — seguro contra clobber,
pero incapaz de actualizar los valores por defecto.

`usersettings.json` se lee con `reloadOnChange: true` + `IOptionsMonitor`, y el servicio se
resuscribe a los cambios **sin reiniciarse**: el cliente es un usuario común y no puede reiniciar
un servicio de Windows. `appsettings.json` no necesita recarga en caliente.

### 3.1 `appsettings.json` (SDigitales)

```jsonc
{
  "General": { "Language": "es" },

  "Monitoring": {
    "DefaultIntervalSeconds": 30,
    "JitterPercent": 20,          // desincroniza instalaciones: no golpean todas en el mismo tick
    "FailuresToAlert": 3,         // fallos consecutivos antes de declarar caída
    "SuccessesToRecover": 2,      // éxitos consecutivos antes de declarar recuperación
    "ReminderIntervalMinutes": 20,
    "DefaultTimeoutMs": 10000,

    "Canary": {
      "Enabled": true,
      "Targets": ["gateway", "8.8.8.8", "https://www.google.com/generate_204"],
      "//": "Si el canario también falla, el incidente se clasifica como 'sin internet' y NO como servicio caído."
    },

    // Perfil ARCA precargado. El cliente puede editar la URL o desactivar un endpoint desde el
    // Viewer; eso se guarda en usersettings.json y sobreescribe esta entrada por Id.
    //
    // DICCIONARIO, no lista (cambio hecho al implementar el prototipo): IConfiguration combina
    // arrays por índice — usersettings.json terminaría pisando la entrada equivocada — pero
    // combina objetos por clave. Con el id como clave, el merge por Id sale gratis.
    "Endpoints": {
      "arca-wsfev1": {
        "Name": "ARCA — Facturación Electrónica (WSFEv1)",
        "Group": "ARCA",
        "Enabled": true,
        "Type": "SoapDummy",              // Http | Wsdl | SoapDummy | Tcp | Ping
        "Url": "https://.../wsfev1/service.asmx",
        "IntervalSeconds": 30,
        "TimeoutMs": 10000,
        "LatencyWarnMs": 3000,            // por encima => estado DEGRADADO
        "Profile": "arca-wsfev1",         // entrada de profiles.json
        "MaintenanceWindows": [ { "Days": "Daily", "From": "02:00", "To": "04:00" } ]
      }
      // + arca-wsaa (autenticación) — ver §12
    }
  },

  "Alerting": {
    "GroupByIncident": true,        // una alerta por incidente, no una por URL
    "GroupingWindowSeconds": 60,
    "SuppressWhenOffline": true,    // sin internet no se alerta ni se abre incidente (§6)
    "MaxMailsPerHour": 12           // circuit breaker: un bug no puede quemar la casilla
  },

  "Smtp": {
    "Host": "mail.tu-dominio.com",
    "Port": 587,
    "UseSsl": true,
    "Username": "alertas@tu-dominio.com",
    "ProtectedPassword": "<blob DPAPI LocalMachine en Base64>",   // ver §7.1
    "FromAddress": "alertas@tu-dominio.com",
    "FromDisplayName": "WebServiceAlerter",
    "RetryCount": 3,
    "RetryBackoffSeconds": 5,
    "TimeoutMilliseconds": 15000
  },

  "Http":     { "IgnoreCertificateErrors": false, "UserAgent": "WebServiceAlerter/1.0",
                "CertificateExpiryWarnDays": 15 },
  "Database": { "Path": "%ProgramData%\\WebServiceAlerter\\webservicealerter.db", "RetentionDays": 90 },
  "FileLogging": { "Directory": "logs", "MaxFileSizeMb": 10, "RetentionDays": 30 },
  "StatusFile":  { "Enabled": true, "Path": "%ProgramData%\\WebServiceAlerter\\status.json" }
}
```

### 3.2 `usersettings.json` (el cliente, vía Viewer)

Lo único que el cliente ve y edita:

```jsonc
{
  "General": { "SiteName": "Estudio Contable XYZ" },   // aparece en el asunto de las alertas

  "Recipients": "contador@estudio.com, soporte@estudio.com",   // lista separada por comas

  "Monitoring": {
    "Endpoints": {
      "arca-wsfev1": { "Url": "https://otra.url/service.asmx" },        // override parcial
      "custom-1": { "Name": "Mi API", "Type": "Http",                   // alta propia
                    "Url": "https://api.cliente.com/health", "Enabled": true }
    }
  }
}
```

**Merge por `Id`:** una entrada de `usersettings.json` con un id existente sobreescribe solo los
campos presentes; un id nuevo agrega un endpoint. El id es la clave estable en la base de datos —
renombrar `Name` no debe perder el historial ni disparar el prune. **Verificado en el prototipo.**

La sección `Smtp` **se ignora deliberadamente** si aparece en `usersettings.json`: el cliente no
puede cambiar el remitente, solo los destinatarios.

---

## 4. Tipos de chequeo (probes)

Interfaz `IProbe` con una implementación por tipo. Cada una devuelve un `ProbeResult` (§5).

### 4.1 `Http`
GET (o HEAD si el server lo admite) a la URL. Éxito = status en `ExpectedStatus`
(default `200-399`). Opcional `MustContain` para validar contenido.

### 4.2 `Wsdl`
GET a `{Url}?WSDL`. Éxito = 200 **y** el cuerpo parsea como XML con raíz `definitions`.
Detecta el caso clásico de un proxy/portal cautivo devolviendo un HTML de error con status 200.

### 4.3 `SoapDummy` — el diferencial
POST de la operación *dummy* sin autenticación que exponen los webservices de ARCA
(y varios otros), parseando el estado de los subsistemas que devuelve:

- WSFEv1 → `FEDummy` ⇒ `AppServer`, `DbServer`, `AuthServer`
- WSFEX → `FEXDummy` — misma forma
- Padrón / otros servicios → `dummy` equivalente

**Caída = HTTP falla _o_ cualquiera de los subsistemas devuelve algo distinto de `OK`.**
Este es el chequeo que distingue "ARCA responde pero la base está caída" de "ARCA anda bien" —
un GET plano reporta verde en ese escenario, que es justamente el que causa el problema.

La configuración de cada dummy (SOAPAction, envelope, nombres de los nodos de respuesta) va en
un archivo de perfiles, no hardcodeada, para poder agregar servicios sin recompilar.

### 4.4 `Tcp` / `Ping`
Conexión TCP a host:puerto, o ICMP. Para servicios no-HTTP y para el canario.

### 4.5 Chequeo transversal: vencimiento del certificado TLS
En cada probe HTTPS se registra los días que faltan para que expire el certificado del endpoint.
Aviso (no caída) por debajo de un umbral configurable. Es prácticamente gratis y evita la
sorpresa clásica de "se cayó todo" que en realidad era un certificado vencido.

---

## 5. Modelo de estados y clasificación de fallas

### 5.1 Resultado de un probe

```csharp
public sealed record ProbeResult
{
    public required string EndpointId { get; init; }
    public required ProbeOutcome Outcome { get; init; }   // ver abajo
    public double? LatencyMs { get; init; }
    public int? HttpStatus { get; init; }
    public string? Detail { get; init; }                  // texto para el mail / la UI
    public int? CertificateDaysToExpiry { get; init; }
    public required DateTimeOffset TimestampUtc { get; init; }
}

public enum ProbeOutcome
{
    Ok,                    // respondió bien y rápido
    Slow,                  // respondió bien pero sobre LatencyWarnMs  -> DEGRADADO
    DnsFailure,            // el nombre no resuelve
    ConnectionRefused,     // TCP rechazado
    TlsFailure,            // handshake / certificado inválido
    Timeout,               // no respondió a tiempo
    HttpError,             // 4xx / 5xx
    ContentMismatch,       // respondió 200 pero el contenido no es el esperado
    ServiceReportedDown,   // el dummy dice que un subsistema no está OK
}
```

**Por qué importa la clasificación:** es literalmente la respuesta a "¿es tu sistema o es ARCA?".
`DnsFailure` + canario caído ⇒ problema de conectividad local. `ServiceReportedDown` ⇒ es ARCA,
inequívocamente. Esa distinción va en el mail, en la UI y en `status.json`.

### 5.2 Estados visibles

`OK` → `DEGRADADO` (Slow) → `CAÍDO` → recuperación → `OK`, más:

- `INTERMITENTE` — **amarillo, igual que el degradado por latencia.** Hubo fallos aislados en los
  últimos N minutos que nunca llegaron al umbral de confirmación, así que no son un incidente,
  pero tampoco son "todo bien". Sin este estado, un servicio que falla una de cada cinco veces se
  ve verde: el usuario no puede facturar y la pantalla le dice que está todo perfecto. Es
  exactamente el escenario observado el 2026-08-12, cuando ARCA fallaba y el monitor no marcaba
  incidente.
- `SIN CONEXIÓN` — el canario está caído. **Estado puramente visual**: se muestra en gris para que
  la pantalla no exhiba un verde viejo, pero no genera alerta, no abre incidente y no descuenta
  uptime. Es un problema local, el usuario ya lo sabe, y el programa no tiene nada que aportar.
- `EN MANTENIMIENTO` — dentro de una ventana declarada: se sigue midiendo y graficando, no se alerta.

### 5.3 Confirmación

La confirmación cuenta **intentos consecutivos** (`FailuresToAlert` / `SuccessesToRecover`), no
tiempo transcurrido como hace ResourceAlerter. Con jitter e intervalos distintos por endpoint,
"viene fallando hace 60 segundos" no dice si fue un intento o diez, y lo que se quiere exigir
antes de despertar a alguien son varios intentos independientes.

> **Cuidado con `MonitorResult.Unavailable`:** en ResourceAlerter significa "no se pudo leer el
> sensor, nunca alertar". Acá un fallo de lectura **es** la alerta. No reusar ese campo con esa
> semántica; en `ProbeResult` no existe.

---

## 6. Canario — compuerta, no fuente de alertas

El canario (gateway + host neutro externo) existe con **un solo propósito**: no declarar caído a
ARCA cuando en realidad se cortó el internet del cliente. No es un monitor más.

Antes de confirmar una caída, se consulta el canario. Si el canario también está caído:

- **No se alerta.** Ni por el endpoint, ni por la falta de conexión.
- **No se abre incidente** y las muestras de ese período **no descuentan uptime** — se marcan como
  no verificables, de modo que un corte de luz del cliente no le arruine la estadística de ARCA.
- La pantalla pasa a gris (`SIN CONEXIÓN`) para no mostrar un verde viejo.
- Al volver la conexión se reanuda el chequeo normal. Si ARCA estaba caído y sigue caído, ahí sí
  se alerta.

Sin esta compuerta, el programa reportaría "ARCA caído" en el momento en que más gente lo está
mirando y en el que es más falso.

---

## 7. Alertas

- **Canal único: SMTP**, con cuenta fija de SDigitales y destinatarios editables por el cliente.
- **Agrupación:** si varios endpoints caen dentro de `GroupingWindowSeconds`, se manda **una**
  alerta que los lista. Un episodio de ARCA no debe producir cuatro mails.
- **Tipos:** `Caída` / `Recordatorio` (cada `ReminderIntervalMinutes`) / `Recuperación`.
  El mail de recuperación incluye la duración total del incidente.
- **Contenido del mail de caída:** sitio, endpoint, causa clasificada (§5.1), status HTTP,
  latencia, detalle del dummy si aplica, hora de inicio, y **estado del canario** — para que se
  entienda de un vistazo de qué lado está el problema.
- **Mantenimiento:** dentro de una ventana declarada no se alerta, pero se sigue midiendo y
  graficando. Si al terminar la ventana el servicio sigue caído, ahí sí alerta.
- **Arranque del servicio:** se mantiene el mail de inicio de ResourceAlerter (lista lo que va a
  monitorear y avisa si la base no pudo abrirse) — es el que detecta configuraciones mudas.

### 7.1 Custodia de la credencial SMTP

La cuenta de envío es de un dominio de SDigitales y vive en el equipo del cliente. El riesgo no es
que lean los mails de alerta: es que **alguien mande correo haciéndose pasar por el dominio**
(phishing, y el dominio en listas negras). Tres medidas, las tres baratas:

1. **Casilla dedicada exclusivamente a alertas** (idealmente en un subdominio propio), sin acceso
   a ningún otro recurso. Si se filtra, se rota esa contraseña y el daño queda acotado.
2. **Contraseña cifrada con DPAPI, scope `LocalMachine`.** El servicio corre como LocalSystem y
   puede descifrarla; y como el blob está atado a esa máquina, **copiar el JSON a otra computadora
   no sirve de nada**. Se agrega un flag `--protect-password` al ejecutable (mismo patrón que los
   `--list-sensors` / `--send-summary` de ResourceAlerter) para generar el blob en la instalación.
   El campo se llama `ProtectedPassword` justamente para que nadie meta ahí texto plano por
   descuido; si el valor no descifra, el servicio lo registra y no envía.
3. **Circuit breaker de envío** (`MaxMailsPerHour`): un bug o un flapping patológico no puede
   quemar la casilla a mailazos y hacer que el proveedor la bloquee.

> Esto **no** hace inviolable la credencial — quien tenga administrador sobre el equipo puede
> descifrarla. Sube el costo de casual a deliberado, y hace que el archivo por sí solo no valga
> nada. Si algún día el riesgo residual molesta, el paso siguiente es una API key de envío
> restringida (solo-enviar, solo ese dominio), que se revoca de a una sin tocar la casilla.

### 7.2 Sin cola de alertas offline

Se evaluó y **se descarta**. Como el servicio no alerta cuando no hay internet (§6), no existe la
alerta varada esperando conexión: toda alerta que se genera se genera con el enlace funcionando.
El `RetryCount` que ya trae `SmtpAlertSender` alcanza para cubrir el caso de borde de que la
conexión se corte durante el envío mismo.

---

## 8. Persistencia

SQLite, mismo patrón que `DataRecorder` (WAL, ACL de `BUILTIN\Users` sobre el directorio para que
el Viewer lea sin elevar, escrituras best-effort que nunca tumban el monitoreo).

```sql
CREATE TABLE Samples (          -- una fila por chequeo
    Id         INTEGER PRIMARY KEY,
    Timestamp  INTEGER NOT NULL,   -- unix UTC
    EndpointId TEXT    NOT NULL,
    LatencyMs  REAL,               -- NULL si no respondió
    Outcome    INTEGER NOT NULL,   -- ProbeOutcome
    HttpStatus INTEGER
);
CREATE INDEX IX_Samples_Endpoint_Time ON Samples(EndpointId, Timestamp);
-- Outcome incluye 'NotVerifiable' (canario caído): se registra para que el gráfico muestre el
-- hueco, pero se excluye del denominador al calcular uptime — un corte de internet del cliente
-- no debe descontarle disponibilidad a ARCA.

CREATE TABLE Incidents (           -- solo caídas reales del servicio; nunca cortes de internet
    Id          INTEGER PRIMARY KEY,
    EndpointId  TEXT    NOT NULL,
    StartedAt   INTEGER NOT NULL,
    ResolvedAt  INTEGER,
    Outcome     INTEGER NOT NULL,  -- causa dominante
    Detail      TEXT
);
CREATE INDEX IX_Incidents_Time ON Incidents(StartedAt);
```

`FillGaps` (los puntos en cero para que el gráfico no interpole sobre períodos sin datos) se
reusa tal cual: acá aplica igual cuando el servicio estuvo parado.

Retención por días, purga diaria — igual que hoy.

---

## 9. Viewer

**El canal principal, no un accesorio** (§7.2): es el único aviso con entrega garantizada. El
usuario tiene que resolver la duda en dos segundos, sin leer.

Restricciones que lo condicionan todo:

- **Corre sin elevación.** Nada de reiniciar servicios ni escribir en Program Files. Guarda en
  `usersettings.json` bajo ProgramData y el servicio lo recarga solo (§3).
- **Pocos endpoints, una sola máquina por cliente.** El semáforo se diseña para 1–5 entradas
  legibles de lejos, no para una grilla de cincuenta filas.

1. **Semáforo** — panel grande, una fila por endpoint, verde / amarillo / rojo / gris, con
   la causa en texto plano: *"ARCA — Facturación: CAÍDO desde las 10:32 (hace 45 min) — el
   servidor de base de datos de ARCA no responde"*.
2. **Ícono en la bandeja** que cambia de color, con notificación toast al cambiar de estado. Es
   lo que hace que el usuario se entere sin tener la ventana abierta. Los íconos **ya están
   generados** (`assets/tray-*.ico`, ver `tools/IconGenerator`):
   - **verde** — todo OK
   - **amarillo** — latencia alta **o fallas intermitentes** (ver §5.2)
   - **rojo** — caído
   - **gris** — sin conexión
3. **Gráfico de latencia** (últimas 24 h / 7 d), con bandas rojas sobre los períodos de caída.
   Mismo espíritu que el Viewer de ResourceAlerter, que es la referencia visual acordada.
4. **Historial de incidentes** — tabla con inicio, duración, causa; y % de uptime por día/semana.
   Esto es lo que el cliente le muestra a su contador cuando pregunta por qué no facturó.
5. **Configuración** — solo lo que es del cliente: **URLs a monitorear** y **lista de mails
   destino** (separada por comas). Sin pantalla de SMTP: la cuenta de envío es fija y no se
   muestra. Reusa el patrón `ConfigStore`; **no** hace falta `ServiceRestarter` (recarga en
   caliente).
   - **Botón "probar ahora"** por endpoint: ejecuta el probe en el momento y muestra el resultado
     crudo. Es lo que evita el "lo cargué y no sé si anda".
   - **Botón "enviar mail de prueba"**: obligatorio, no opcional. Como el cliente escribe los
     destinatarios a mano, un typo deja la instalación muda para siempre y nadie se entera hasta
     la caída real. Se valida el formato al guardar y se confirma con un envío real.

---

## 10. Archivo de estado (`status.json`)

El servicio escribe el estado actual a un JSON local en cada ciclo. Permite que **el sistema de
facturación lo lea antes de intentar emitir** y muestre "ARCA caído hace 10 minutos, no
reintentes" en vez de dejar al operador colgado esperando un timeout.

```jsonc
{
  "updatedAt": "2026-08-11T10:32:14-03:00",
  "internet": "ok",
  "endpoints": [
    { "id": "arca-wsfev1", "name": "ARCA — Facturación Electrónica",
      "state": "down", "since": "2026-08-11T10:32:14-03:00",
      "outcome": "ServiceReportedDown", "detail": "DbServer=ERROR",
      "latencyMs": null, "uptime24h": 0.982 }
  ]
}
```

Es lo que convierte al alerter de "reporta el problema" en "evita el llamado".

---

## 11. Plan de implementación

| Fase | Alcance | Entregable verificable |
|---|---|---|
| 1 ◐ | Esqueleto: servicio, config en dos capas, logging, SQLite, probes `Http`/`Wsdl`/`Tcp`, loop con jitter | **Hecho en v0.1**, con una salvedad: la recarga en caliente anda para los destinatarios, pero el `Worker` resuelve los endpoints una sola vez al arrancar, así que agregar o cambiar una URL todavía requiere reiniciar el servicio. **Hay que cerrarlo antes de la fase 4**, porque el cliente no puede reiniciar servicios |
| 2 ✅ | Máquina de estados por conteo, canario como compuerta, mail agrupado, DPAPI + `--protect-password` | **Hecho en v0.1**, salvo prueba de campo del envío real de mail |
| 3 ◐ | Probe `SoapDummy` + perfiles de servicios + perfil ARCA precargado | **Motor hecho y probado end to end** contra un servicio SOAP público; falta la URL real de ARCA |
| 4 | Viewer: semáforo, bandeja, gráfico, incidentes, edición de URLs y destinatarios, mail de prueba | Instalable y usable por el cliente sin elevación |
| 5 | `status.json`, ventanas de mantenimiento, aviso de certificado, instalador WiX | Paquete de instalación |

---

## 12. Puntos resueltos y pendientes

### Resueltos (2026-08-11)

| # | Tema | Resolución |
|---|---|---|
| 1 | URLs de ARCA | El usuario las confirma; **quedan editables** por el cliente y actualizables por SDigitales (§3) |
| 2 | Otros servicios | El motor queda genérico, pero **con que funcione para ARCA alcanza** para la v1 |
| 3 | Proxy | **Descartado**, fuera de alcance |
| 4 | Escala | **Una instalación por cliente, en un único equipo**; pocos endpoints |
| 5 | Canales y configuración | **Solo mail**, cuenta SMTP fija de SDigitales; el cliente edita **solo URLs y destinatarios**. Discord afuera |

### Pendientes

1. **URLs reales de ARCA (bloqueante para la fase 3).** Con el cambio AFIP→ARCA conviven dominios
   viejos y nuevos; hay que verificarlas contra la documentación vigente, no cargarlas de memoria.
   Falta confirmación de cuáles usan los clientes hoy, en producción y homologación.
2. **¿Cuántos endpoints trae el perfil ARCA?** La respuesta "1 por instalación" se leyó como *un
   equipo por cliente*. Para el caso de facturación conviene monitorear **al menos dos**:
   - **WSFEv1** — el servicio de facturación propiamente dicho.
   - **WSAA** — la autenticación. Si WSAA está caído no se puede obtener el ticket de acceso, así
     que **no se factura aunque WSFEv1 esté verde**. Sin este endpoint, el semáforo puede mostrar
     todo en verde mientras el cliente no logra emitir — exactamente el llamado que el programa
     busca evitar.

   Falta confirmar si se monitorean ambos (recomendado) o solo WSFEv1.
3. **Datos de la casilla de envío**: host SMTP, puerto, y la casilla dedicada a crear (§7.1).
   Necesarios recién para la fase 2.

