<p align="center">
  <img src="assets/icon-256.png" width="128" alt="WebServiceAlerter">
</p>

<h1 align="center">WebServiceAlerter</h1>

<p align="center">
  Monitor de disponibilidad y latencia de webservices, pensado para facturación electrónica.<br>
  Responde una sola pregunta, y la responde bien: <b>¿el problema es mi sistema o es el servicio?</b>
</p>

<p align="center">
  <img src="assets/tray-states.png" width="320" alt="Estados: OK, degradado, caído, sin conexión">
</p>

---

## El problema

Cuando se cae el webservice de facturación electrónica de ARCA, todos los clientes reportan al
mismo tiempo y el soporte se vuelve un infierno — porque nadie puede distinguir "se cayó ARCA" de
"se rompió el sistema de facturación".

WebServiceAlerter se instala en el equipo del cliente, chequea los servicios cada pocos segundos y
muestra un semáforo. El usuario mira la pantalla y sabe de qué lado está el problema, sin llamar a
nadie. Si el servicio se cae, avisa por mail; cuando se recupera, también.

> **Estado: prototipo (v0.1).** El servicio de monitoreo funciona y es usable desde consola.
> Falta el Viewer (semáforo, gráfico, bandeja) y el instalador. Ver [ESPECIFICACION.md](ESPECIFICACION.md).

## Lo que lo diferencia de un ping

**1. Pregunta por la salud real del servicio, no si el puerto contesta.**
ARCA expone un método `Dummy` sin autenticación que informa el estado de sus subsistemas
(`AppServer`, `DbServer`, `AuthServer`). Cuando su base de datos está caída el endpoint **sigue
devolviendo HTTP 200** y el WSDL sigue descargando perfecto: un chequeo de disponibilidad común
informa todo verde mientras nadie puede facturar. Leer la respuesta del dummy es la única forma
barata y sin credenciales de ver la diferencia.

**2. No miente cuando el problema es local.**
Antes de declarar caído a un servicio consulta un canario (gateway + host externo). Si no hay
internet no alerta, no abre incidente y no descuenta uptime: un corte en la oficina del cliente no
puede disfrazarse de caída de ARCA. Es el falso positivo más dañino que puede tener una
herramienta así, porque aparece justo cuando todos están mirando la pantalla.

**3. Dice *por qué* falló.** DNS, conexión rechazada, TLS, timeout, error HTTP, contenido
inesperado o "el servicio informa que está caído" son cosas distintas y se reportan distinto.

**4. Una alerta por incidente, no una por URL.** El proyecto nace de una avalancha de avisos
simultáneos; un monitor que responde a una caída con un mail por endpoint sólo mueve la avalancha
de lugar.

## Probarlo

Necesitás el [SDK de .NET 10](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/<usuario>/WebServiceAlerter.git
cd WebServiceAlerter/src/WebServiceAlerter
dotnet run -- --once
```

Comandos disponibles:

| Comando | Qué hace |
|---|---|
| `--once` | Chequea todo una vez y muestra el resultado. Lo más rápido para ver si sirve. |
| `--list` | Lista los endpoints configurados. |
| `--test <id>` | Chequea un solo endpoint, con el detalle crudo. |
| `--test-mail` | Manda un mail de prueba a los destinatarios configurados. |
| `--protect-password` | Cifra la contraseña SMTP con DPAPI para esta máquina. |
| *(sin argumentos)* | Corre el loop de monitoreo (consola, o como servicio de Windows). |

## Configuración

Dos archivos, separados por **dueño** y no por máquina:

| Archivo | Dónde | Quién lo edita | En una actualización |
|---|---|---|---|
| `appsettings.json` | Junto al ejecutable | Quien distribuye | **Se sobrescribe** |
| `usersettings.json` | `%ProgramData%\WebServiceAlerter\` | El cliente | **Nunca se toca** |

`usersettings.json` se relee **en caliente**: el cliente es un usuario común y no puede reiniciar
un servicio de Windows, así que sus cambios tienen que aplicarse solos.

Los endpoints son un diccionario y no una lista, a propósito: `IConfiguration` combina listas por
índice (y pisaría la entrada equivocada) pero combina objetos por clave. Así, esto en
`usersettings.json` cambia sólo la URL y deja el resto de la definición intacta:

```jsonc
{
  "Alerting": { "Recipients": "contador@estudio.com, soporte@estudio.com" },
  "Monitoring": {
    "Endpoints": {
      "arca-wsfev1": { "Url": "https://..." }
    }
  }
}
```

Los archivos de configuración admiten comentarios `//`.

## Seguridad — leer antes de distribuir

La casilla de envío es fija y pertenece a quien distribuye el programa, no al cliente. El riesgo
no es que alguien lea los mails de alerta: es que **levante las credenciales de un equipo ajeno y
mande phishing desde tu dominio**. Por eso:

- **La contraseña nunca va en texto plano.** Se guarda en `Smtp:ProtectedPassword`, cifrada con
  DPAPI en scope `LocalMachine`, generada con `--protect-password` **en cada equipo**. El blob
  está atado a esa máquina: copiar el `appsettings.json` a otra computadora no sirve de nada.
- **Usá una casilla dedicada sólo a alertas**, sin acceso a ningún otro recurso. Si se filtra,
  rotás esa contraseña y listo.
- **Hay un tope de mails por hora**, para que un endpoint inestable o un bug no queme la casilla.

Esto **no** vuelve inviolable la credencial: quien tenga administrador sobre el equipo puede
descifrarla. Sube el costo de casual a deliberado, que para un archivo que vive en decenas de
escritorios ajenos es la diferencia que importa.

> `appsettings.Local.json`, `usersettings.json` y los `.db` están en `.gitignore`. **Nunca**
> commitees credenciales reales.

## Cómo colaborar

El aporte más valioso no requiere escribir una línea de C#: **agregar perfiles de servicio a
[`profiles.json`](src/WebServiceAlerter/profiles.json)**. Un perfil describe cómo preguntarle a un
webservice si está vivo — el sobre SOAP, el `SOAPAction` y qué nodos de la respuesta mirar. Si
monitoreás un banco, una pasarela de pago u otro organismo con método `dummy`, agregá una entrada
y mandá un pull request.

```jsonc
"mi-servicio": {
  "Description": "Qué es y de dónde salió la definición",
  "SoapAction": "http://ejemplo/Dummy",
  "Envelope": "<?xml version=\"1.0\"?><soap:Envelope ...>...</soap:Envelope>",
  "StatusNodes": [ "AppServer", "DbServer" ],
  "OkValue": "OK"
}
```

También sirve mucho: probarlo y reportar qué se rompe, y contar **qué monitoreás vos**, para que
el diseño no quede sesgado únicamente a ARCA.

### Pendiente

- **Confirmar las URLs vigentes de ARCA** (producción y homologación). Vienen deshabilitadas y con
  `"Url": "COMPLETAR"` a propósito: es preferible no monitorear nada antes que monitorear una URL
  inventada y reportar "caído" para siempre.
- Viewer (semáforo, ícono de bandeja, gráfico de latencia, historial de incidentes).
- Instalador.

## Arquitectura

```
Worker ──> ProbeRunner ──> HttpProbe / WsdlProbe / SoapDummyProbe / TcpProbe
   │            │
   │            └─ profiles.json  (datos, no código)
   │
   ├─> CanaryChecker      compuerta: ¿hay internet? Si no, no se juzga nada
   ├─> EndpointTracker    máquina de estados por endpoint (fallos consecutivos)
   ├─> AlertDispatcher    agrupa transiciones y manda un mail por ciclo
   ├─> DataRecorder       SQLite: muestras e incidentes
   └─> StatusWriter       status.json, para que otro programa lea el estado
```

`status.json` existe para integrar: el sistema de facturación puede leerlo antes de intentar
emitir y avisar "ARCA caído hace 10 minutos, no reintentes", en vez de dejar al operador esperando
un timeout.

## Licencia

MIT — ver [LICENSE](LICENSE).
