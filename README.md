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

> **Estado: v0.3.0.** Servicio, Viewer e instalador MSI andando y probados contra ARCA en
> producción. Ver [CHANGELOG.md](CHANGELOG.md) y [ESPECIFICACION.md](ESPECIFICACION.md).

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
git clone https://github.com/asantarelli/WebServiceAlerter.git
cd WebServiceAlerter/src/WebServiceAlerter
dotnet run -- --once
```

> Para **instalarlo** en un equipo no hace falta compilar nada: bajá el MSI de la
> [última versión](https://github.com/asantarelli/WebServiceAlerter/releases/latest) y seguí la
> [instalación paso a paso](#instalación-paso-a-paso).

Comandos disponibles:

| Comando | Qué hace |
|---|---|
| `--once` | Chequea todo una vez y muestra el resultado. Lo más rápido para ver si sirve. |
| `--list` | Lista los endpoints configurados. |
| `--test <id>` | Chequea un solo endpoint, con el detalle crudo. |
| `--history [horas]` | Qué registró el monitor en ese período: disponibilidad, incidentes y fallos sueltos. Es el comando para cuando alguien reporta "no pude facturar a las diez y cuarto". |
| `--test-mail` | Manda un mail de prueba a los destinatarios configurados. |
| `--protect-password` | Cifra la contraseña SMTP con DPAPI y la guarda en `%ProgramData%\WebServiceAlerter\smtp.json`. Una vez por equipo. |
| `--configure-smtp` | Carga la casilla de envío completa en `smtp.json`. Una vez por equipo, en consola elevada. |
| `--configure-discord` | Configura el canal de Discord: identidad e URL del webhook, cifrada. Una vez por equipo. |
| `--test-discord` | Publica un mensaje de prueba en Discord. |
| *(sin argumentos)* | Corre el loop de monitoreo (consola, o como servicio de Windows). |

## Instalación paso a paso

Todo esto se hace **una sola vez por equipo**. Lleva unos minutos.

### 1. Instalar

Bajá el MSI de la [última versión](https://github.com/asantarelli/WebServiceAlerter/releases/latest)
y ejecutalo, o desde consola:

```bash
msiexec /i WebServiceAlerter-0.3.0-win-x64.msi
```

Instala el servicio (arranque automático), el Viewer y los accesos directos en el escritorio y el
menú de inicio. **No hace falta tener .NET instalado**: las dos aplicaciones van autocontenidas.

Si ya había una versión anterior, se instala encima sin desinstalar primero.

### 2. Configurar la casilla de envío

Abrí una consola **como administrador** — hace falta, porque los archivos de configuración
pertenecen al servicio — y corré:

```bash
"C:\Program Files\WebServiceAlerter\WebServiceAlerter.exe" --configure-smtp
```

Va a pedir, en este orden:

| Dato | Ejemplo | Notas |
|---|---|---|
| Servidor SMTP | `mail.tu-dominio.com` | |
| Puerto | `587` | Enter deja 587 |
| ¿Usa SSL/TLS? | `s` / `n` | Depende del servidor; probá con el que use tu proveedor |
| Usuario | `alertas@tu-dominio.com` | |
| Dirección remitente | *(Enter usa el usuario)* | **Conviene que coincida con el usuario**: muchos servidores rechazan el envío si difieren |
| Nombre visible | `WebServiceAlerter` | |
| Contraseña | *(no se muestra al tipear)* | Dejarla vacía conserva la ya guardada |

**Este paso no es opcional.** El instalador se publica abierto, así que no puede llevar los datos
de la casilla adentro y los instala vacíos: sin cargarlos la instalación queda muda, y eso recién
se nota cuando hace falta avisar de una caída.

La contraseña se cifra con DPAPI contra esa máquina y se guarda en
`%ProgramData%\WebServiceAlerter\smtp.json`, fuera del directorio de instalación, donde las
actualizaciones no lo pisan.

### 3. Cargar los destinatarios

Abrí el **Viewer** (acceso directo del escritorio) y entrá en **Configuración**. Ahí van:

- **Nombre de este equipo** — aparece en el asunto de cada alerta, para saber de qué instalación
  viene.
- **Mails de destino** — separados por coma.
- **Sensibilidad** — cada cuántos segundos se chequea, cuántos fallos seguidos hacen falta para
  avisar, cuántos aciertos para dar por recuperado, y cada cuánto repetir el aviso.

El servicio relee todo eso **en caliente**, sin reiniciar.

### 4. Probar que el mail sale

Desde la misma pantalla de Configuración, botón **Enviar mail de prueba**. O por consola:

```bash
"C:\Program Files\WebServiceAlerter\WebServiceAlerter.exe" --test-mail
```

Si no llega, el motivo aparece en pantalla. Los dos errores más comunes son una dirección de
destino mal tipeada y que el remitente no coincida con el usuario autenticado.

### 5. Discord (opcional)

Sólo si querés publicar los eventos en un canal compartido entre desarrolladores. En consola
**como administrador**:

```bash
"C:\Program Files\WebServiceAlerter\WebServiceAlerter.exe" --configure-discord
```

Pide la **identidad** de este equipo en el canal —localidad e ISP, por ejemplo
`Rosario, Santa Fe — Telecom`, nunca el nombre del cliente— y la **URL del webhook**, que se
obtiene en Discord con *Editar canal → Integraciones → Webhooks*.

Para comprobarlo:

```bash
"C:\Program Files\WebServiceAlerter\WebServiceAlerter.exe" --test-discord
```

### 6. Verificar que quedó midiendo

```bash
"C:\Program Files\WebServiceAlerter\WebServiceAlerter.exe" --history 1
```

Tiene que mostrar chequeos registrados. También podés mirar el semáforo en el Viewer, o el ícono
de la bandeja, que queda verde cuando todo responde.

### Desinstalar

Desde *Aplicaciones instaladas* de Windows, o `msiexec /x`. **No borra** la base de datos ni la
configuración de `%ProgramData%\WebServiceAlerter`: dar de baja el programa no es lo mismo que
querer perder el historial. Para eliminarlos, borrá esa carpeta a mano.

## Compilar el instalador

Sólo hace falta si trabajás sobre el código.

```bash
.\installer\build-installer.ps1
```

Publica las dos aplicaciones y arma el MSI. La primera vez, `dotnet tool restore` para bajar WiX,
que viene fijado como herramienta local del repo.

> Se usa **WiX 5** y no 7 a propósito: la 7 exige adherir al *Open Source Maintenance Fee*, que
> para uso comercial implica pagar. La 5 es libre y entiende el mismo esquema.

## Configuración

Cuatro archivos, separados por **dueño** y no por máquina:

| Archivo | Dónde | Quién lo edita | En una actualización |
|---|---|---|---|
| `appsettings.json` | Junto al ejecutable | Quien distribuye | **Se sobrescribe** |
| `usersettings.json` | `%ProgramData%\WebServiceAlerter\` | El cliente, desde la pantalla | **Nunca se toca** |
| `smtp.json` | `%ProgramData%\WebServiceAlerter\` | Se genera con `--configure-smtp` | **Nunca se toca** |
| `discord.json` | `%ProgramData%\WebServiceAlerter\` | Se genera con `--configure-discord` | **Nunca se toca** |

Ese reparto es lo que hace que actualizar sea seguro: los valores por defecto y los perfiles se
pueden corregir en una versión nueva sin pisar nada de lo que el cliente configuró, y sin dejarlo
sin credenciales.

El Viewer trae una pantalla de configuración con el nombre del equipo, los destinatarios y la
sensibilidad de las alertas. Sólo expone parámetros que el servicio relee en caliente: el cliente
no puede reiniciar un servicio de Windows, así que un campo que exigiera reinicio sería una
promesa falsa.

`usersettings.json` se relee en caliente, porque el cliente es un usuario común y no puede
reiniciar un servicio de Windows. **En la v0.1 esto anda para los destinatarios pero todavía no
para los endpoints**: el loop resuelve la lista una sola vez al arrancar, así que agregar o
cambiar una URL requiere reiniciar. Está pendiente y hay que cerrarlo antes del Viewer.

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

## Discord: vista común entre desarrolladores

Además del mail al cliente, el monitor puede publicar cada evento en un canal de Discord
compartido. El propósito es distinto: el mail le avisa al dueño del equipo, mientras que Discord
arma una vista del estado de los servicios en muchas instalaciones a la vez. Cuando ARCA se cae,
ahí se ve en el momento si le está pasando a todos o a uno solo.

```bash
"C:\Program Files\WebServiceAlerter\WebServiceAlerter.exe" --configure-discord
```

Pide dos cosas: la **identidad** de ese equipo en el canal y la **URL del webhook**.

**La identidad es localidad e ISP, nunca el nombre del cliente** — por ejemplo
`Rosario, Santa Fe — Telecom`. El canal lo ven varios desarrolladores y no corresponde que sepan
de qué empresa es cada servidor.

Eso no depende de acordarse de borrar campos: el aviso viaja sin redactar y cada canal lo escribe
con su propia configuración, así que el sender de Discord **no tiene acceso** al nombre de la
instalación. Tampoco publica el nombre del equipo (suele ser el de la empresa) ni las URLs
monitoreadas (un endpoint propio del cliente delataría su dominio).

> **Hasta dónde llega:** en una localidad chica con un solo cliente, la identidad más el horario
> puede alcanzar para deducir de quién se trata. Protege del vistazo casual, no de alguien que
> quiera averiguarlo.

La URL del webhook se guarda cifrada con DPAPI en `discord.json`, porque es un secreto —quien la
tenga puede escribir en el canal— y vive en equipos de clientes. Por lo mismo **no viaja dentro
del instalador**, que se publica abierto.

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
