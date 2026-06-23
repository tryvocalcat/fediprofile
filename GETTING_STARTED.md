# Getting Started with FediProfile

## Prerequisites

Before using **FediProfile**, make sure you have the following tools installed:

- Git
- .NET 9

---

# Step 1: Install Git

> If you already have Git installed, you can skip this step.

1. Go to the official Git website:

```txt
https://git-scm.com/install/windows
```

2. Download the version that matches your operating system.

3. Run the installer as administrator.

4. Follow the installation wizard:
   - Click **Next**
   - You can keep the default installation path
   - Optionally select **Create Desktop Icon**
   - Continue clicking **Next** until you reach **Install**

5. Once the installation is complete, click **Finish**.

---

# Step 2: Install .NET 9

> If you already have .NET 9 installed, you can skip this step.

1. Go to the official .NET 9 website:

```txt
https://dotnet.microsoft.com/en-us/download/dotnet/9.0
```

2. Download the version that matches your operating system.

3. Run the installer as administrator.

4. Click **Install** and wait for the process to finish.

5. Once completed, click **Close**.

---

# Step 3: Verify Git and .NET Installation

1. Press:

```bash
Windows + R
```

2. Type:

```bash
cmd
```

3. Run the following commands:

```bash
dotnet --version
git --version
```

If both commands display a version number, the installation was successful.

---

# Step 4: Clone the Repository

1. Open the FediProfile repository:

```txt
https://github.com/tryvocalcat/fediprofile/tree/main
```

2. Click the **Code** button and copy the repository URL.

3. Create a folder named:

```txt
fediprofile
```

4. Inside the folder:
   - Right-click
   - Select **Open Git Bash here**

5. Initialize Git:

```bash
git init
```

6. Clone the repository:

```bash
git clone <REPOSITORY_URL>
```

Example:

```bash
git clone https://github.com/tryvocalcat/fediprofile.git
```

7. Wait for the cloning process to finish.

---

# Step 5: Run the Project

1. Enter the project folder:

```bash
cd fediprofile
```

2. Open **Git Bash** again inside the folder.

3. Run the project:

```bash
dotnet run
```

---

# Step 6: Open the Application

Once the project is running, a local address will appear in the terminal, for example:

```txt
http://localhost:5000
```

Open that address in your browser.

---

# Done!

You are now ready to use **FediProfile** 