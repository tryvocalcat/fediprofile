# Cómo usar FediProfile

## Requisitos previos

Antes de comenzar a usar **FediProfile**, es necesario instalar las siguientes herramientas:

- Git
- .NET 9

---

# Paso 1: Instalar Git

> Si ya tienes Git instalado, puedes omitir este paso.

1. Ve a la página oficial de Git:

```txt
https://git-scm.com/install/windows
```

2. Descarga la versión correspondiente a tu sistema operativo.

3. Ejecuta el instalador como administrador.

4. Sigue el asistente de instalación:
   - Haz clic en **Next**
   - Puedes dejar la ruta por defecto
   - Opcionalmente selecciona **Create Desktop Icon**
   - Continúa dando clic en **Next** hasta llegar a **Install**

5. Una vez finalizada la instalación, haz clic en **Finish**.

---

# Paso 2: Instalar .NET 9

> Si ya tienes .NET 9 instalado, puedes omitir este paso.

1. Ve a la página oficial de .NET 9:

```txt
https://dotnet.microsoft.com/es-es/download/dotnet/9.0
```

2. Descarga la versión correspondiente a tu sistema operativo.

3. Ejecuta el instalador como administrador.

4. Haz clic en **Install** y espera a que termine el proceso.

5. Cuando finalice, haz clic en **Close**.

---

# Paso 3: Verificar instalación de Git y .NET

1. Presiona:

```bash
Windows + R
```

2. Escribe:

```bash
cmd
```

3. Ejecuta los siguientes comandos:

```bash
dotnet --version
git --version
```

Si ambos comandos muestran una versión, la instalación fue correcta.

---

# Paso 4: Clonar el repositorio

1. Accede al repositorio de FediProfile:

```txt
https://github.com/tryvocalcat/fediprofile/tree/main
```

2. Haz clic en el botón **Code** y copia el enlace del repositorio.

3. Crea una carpeta llamada:

```txt
fediprofile
```

4. Dentro de la carpeta:
   - Haz clic derecho
   - Selecciona **Open Git Bash here**

5. Inicializa Git:

```bash
git init
```

6. Clona el repositorio:

```bash
git clone <URL_DEL_REPOSITORIO>
```

Ejemplo:

```bash
git clone https://github.com/tryvocalcat/fediprofile.git
```

7. Espera a que termine la clonación.

---

# Paso 5: Ejecutar el proyecto

1. Entra a la carpeta del proyecto:

```bash
cd fediprofile
```

2. Abre nuevamente **Git Bash** dentro de la carpeta.

3. Ejecuta el proyecto:

```bash
dotnet run
```

---

# Paso 6: Abrir la aplicación

Cuando el proyecto esté en ejecución, aparecerá una dirección local en la terminal, por ejemplo:

```txt
http://localhost:5000
```

Abre esa dirección en tu navegador.

---

# ¡Listo!

Ya puedes comenzar a usar **FediProfile**