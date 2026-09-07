# Registro de cambios

Este proyecto usa [versionado semántico](https://semver.org/lang/es/).

## [0.3.1] — 2026-09-07

- **Dos casillas en la pantalla de configuración** para encender o apagar, por equipo, el envío de
  mail y la publicación en Discord.

  Permite instalar el mismo programa de dos maneras en un cliente: en el **servidor**, con los
  avisos encendidos; y en las **terminales**, apagados, para que puedan ver el semáforo y el
  gráfico sin que una misma caída dispare un aviso por máquina.

- La casilla de Discord aparece deshabilitada y así rotulada si el equipo no tiene el canal
  configurado, en vez de dejar marcar algo que no haría nada.
- Apagar Discord desde la pantalla no borra la identidad ni el webhook: quedan guardados para
  cuando se vuelva a encender.
- Probar el envío con la casilla destildada avisa que el canal está apagado, en lugar de fallar
  sin causa visible.

> **Nota para instalar en terminales:** cada instalación monitorea por su cuenta, así que N
> terminales generan N veces las consultas a los servicios monitoreados desde la misma conexión.
> Conviene subirles el intervalo de chequeo desde la pantalla —60 o 120 segundos— y dejar el
> intervalo corto sólo en el servidor, que es el que avisa.

## [0.3.0] — 2026-08-14

### Discord

- **Cada evento se puede publicar en un canal de Discord compartido**, además del mail al cliente.
  El propósito es distinto: el mail avisa al dueño del equipo; Discord arma una vista del estado
  de los servicios en muchas instalaciones a la vez, para ver si una caída le está pasando a todos
  o a uno solo.
- **La instalación se identifica por localidad e ISP, nunca por el cliente.** El canal lo ven
  varios desarrolladores y no corresponde que sepan de qué empresa es cada servidor.
- La anonimización es estructural: el aviso viaja sin redactar y cada canal lo escribe con su
  propia configuración, así que el sender de Discord no recibe el nombre de la instalación y no
  puede filtrarlo. Tampoco publica el nombre del equipo ni las URLs monitoreadas.
- Sin identidad configurada no se publica nada, en vez de caer al nombre de la instalación.
- Sólo se aceptan webhooks de `discord.com`, con la misma validación al configurar y al enviar.
- La URL del webhook se guarda cifrada con DPAPI en `discord.json` bajo `%ProgramData%`: es un
  secreto, vive en equipos de clientes y no puede viajar dentro del MSI, que se publica abierto.
- Tope de mensajes por hora y respeto del `429` de Discord, que en un canal alimentado por muchas
  instalaciones es lo esperable justo cuando se cae un servicio que todos monitorean.
- Comandos nuevos: `--configure-discord` y `--test-discord`.

### Configuración de la casilla de envío

- **`--configure-smtp`**: carga servidor, puerto, SSL, usuario, remitente y contraseña en
  `smtp.json`, de una vez. Cierra la limitación de la 0.2.1: el instalador se publica abierto y por
  eso instala la sección `Smtp` vacía, así que sin este paso **una instalación quedaba muda** —no
  podía enviar nada— y el fallo recién aparecía cuando hacía falta avisar de una caída.
- `--protect-password` ya no reescribe el archivo entero: sólo cambia la contraseña. Antes,
  cambiarla borraba el servidor y el usuario.
- Dejar la contraseña vacía en `--configure-smtp` conserva la guardada, para poder corregir el
  servidor sin volver a tipearla.
- El servicio relee la configuración de la casilla **en caliente**. Antes se leía una sola vez al
  arrancar: quien acababa de configurarla probaba en ese mismo momento y la veía fallar.
- Cuando falta permiso para escribir en ProgramData, se explica que hace falta una consola de
  administrador en vez de mostrar un "acceso denegado" pelado.

### Interno

- Los avisos pasan a viajar como evento estructurado (`AlertEvent`) en vez de texto ya armado, y
  cada canal los redacta. Es lo que permite que dos canales usen identidades distintas sin que
  mantenerlas separadas dependa de recordar borrar campos.

## [0.2.1] — 2026-08-13

### Instalador

- **Instalador MSI** con el servicio, el Viewer y los accesos directos. Se instala encima de una
  versión anterior sin desinstalar primero.
- La contraseña SMTP pasa de `appsettings.json` a `smtp.json` bajo `%ProgramData%`. El MSI
  sobrescribe `appsettings.json` en cada actualización —es deliberado, así una versión nueva puede
  corregir una URL o sumar un perfil— pero eso habría borrado el blob DPAPI en cada upgrade,
  dejando al cliente sin poder enviar alertas.
- Se usa WiX 5 y no 7: la 7 exige adherir al *Open Source Maintenance Fee*, que para uso comercial
  implica pagar. Viene fijada como herramienta local del repo.

### Configuración

- **Pantalla de configuración** en el Viewer: nombre del equipo, destinatarios, intervalo entre
  chequeos y sensibilidad de las alertas, con mail de prueba y validación de direcciones.
- **El servicio relee la configuración en caliente**, incluidos los endpoints: se pueden agregar,
  quitar o reconfigurar sin reiniciar. Un endpoint cuya definición no cambió conserva su estado,
  para que releer no borre una caída en curso ni dispare un falso aviso de recuperación.

### Gráfico

- La serie se corta en los períodos sin muestras, que se marcan en gris. Antes unía el último
  punto antes de apagar el equipo con el primero de la mañana siguiente, dibujando una rampa que
  parecía latencia creciendo durante la noche.
- Los chequeos fallidos se grafican como 0 en lugar del tiempo que tardaron en vencer el timeout:
  ese número es el timeout configurado, no una medición.
- El eje se recorta a un límite robusto cuando hay picos extremos, aclarándolo en la leyenda. Con
  mediana de 88 ms y máximas cercanas a 10.000, el eje se iba a la escala del pico y aplastaba
  contra el piso el rango donde el servicio vive el 95 % del tiempo.
- Los incidentes confirmados se dibujan como una banda que cubre toda su duración; el resto de los
  eventos, como barras de color por severidad.

### Herramientas

- `--history` suma percentiles de latencia (mediana, p95, p99 y máxima).

### Limitaciones conocidas

- **Una instalación nueva no puede enviar mail hasta configurar SMTP.** El MSI instala
  `appsettings.json` con la sección `Smtp` vacía, y `--protect-password` sólo guarda la
  contraseña; el servidor, el usuario y la dirección de envío todavía hay que cargarlos a mano.

## [0.1.0] — 2026-08-12

Primera versión. El servicio de monitoreo y el Viewer funcionan y están verificados contra ARCA
real; falta el instalador y la recarga en caliente de endpoints.

### Monitoreo

- Cuatro tipos de chequeo: `Http`, `Wsdl`, `SoapDummy` y `Tcp`.
- **`SoapDummy`** lee el estado de los subsistemas que informa el método dummy del servicio
  (`AppServer`, `DbServer`, `AuthServer` en el caso de ARCA). Es el único chequeo que distingue
  "el endpoint contesta 200 pero su base de datos está caída" de "el servicio anda", una falla que
  cualquier chequeo de disponibilidad común reporta en verde.
- Perfiles de servicio en `profiles.json`, como datos y no como código: sumar un servicio nuevo no
  requiere recompilar.
- Perfil de ARCA precargado: WSFEv1 y WSAA, producción y homologación.
- Clasificación de fallas: DNS, conexión rechazada, TLS, timeout, error HTTP, contenido inesperado
  y servicio informando estar caído.
- Canario como compuerta: sin internet no se alerta, no se abre incidente y no se descuenta
  disponibilidad. Un corte local no puede disfrazarse de caída del servicio remoto.
- Confirmación por conteo de intentos consecutivos, no por tiempo transcurrido.
- Ventanas de mantenimiento, aviso por vencimiento del certificado TLS, y jitter en el intervalo
  para que las instalaciones no golpeen todas en el mismo instante.

### Alertas

- Mail al caer, recordatorio periódico y aviso de recuperación, agrupados en un solo mensaje por
  incidente.
- Contraseña SMTP cifrada con DPAPI en scope `LocalMachine`, atada a la máquina que la generó.
- Tope de mails por hora, para que un endpoint inestable no queme la casilla de envío.

### Interfaz

- Viewer con semáforo, gráfico de latencia de 24 h, historial de incidentes e ícono de bandeja que
  cambia de color.
- Marcas verticales en el gráfico por severidad: amarillo para respuestas lentas, naranja para
  fallos aislados y rojo para los que formaron parte de un incidente confirmado.
- Estado amarillo por **fallas intermitentes**: un endpoint que falla de a ratos sin llegar al
  umbral de confirmación no se muestra en verde.
- Detecta que el monitor dejó de correr en vez de seguir mostrando el último estado conocido.

### Herramientas

- `--once`, `--list`, `--test <id>`, `--history [horas]`, `--test-mail` y `--protect-password`.
- `tools/FakeArca`: simulador que reproduce el escenario que no se puede provocar de otra forma —
  el servicio contestando 200 mientras informa que su base está caída. Se maneja por teclado o por
  HTTP, así que sirve para demostrar y para automatizar.
- Scripts de publicación y de alta/baja del servicio de Windows.

### Verificado contra ARCA real

El monitor capturó una caída de producción de WSFEv1 el 2026-08-12: dejó de responder a las
14:53:40 y volvió 56 segundos después, con dos chequeos en timeout, mientras WSAA seguía al 100 %.
De esa medición salió bajar `FailuresToAlert` de 3 a 2: con intervalo de 30 segundos, un corte de
esa duración produce exactamente dos chequeos fallidos, y con el valor anterior el incidente no se
habría confirmado.

### Limitaciones conocidas

- **Los cambios de endpoints no se releen en caliente.** Los destinatarios sí; agregar o cambiar
  una URL todavía exige reiniciar el servicio.
- **No hay instalador.** El alta se hace con los scripts de `scripts/`, que requieren consola
  elevada.
- El envío de mail está verificado, pero contra un solo servidor SMTP.
