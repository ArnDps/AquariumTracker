# ADAqua / AquariumTracker

ADAqua est une application WPF C# pour gerer des aquariums, leurs parametres d'eau, leurs plantes et leur population.

## Lancement dans Visual Studio

1. Ouvrir `ADAqua.sln`.
2. Verifier que `ADAqua.App` est le projet de demarrage.
3. Si Visual Studio tente de lancer une bibliotheque, faire clic droit sur `ADAqua.App`, puis `Definir comme projet de demarrage`.
4. Lancer le profil `ADAqua.App` pour demarrer sans configuration MySQL obligatoire.
5. Lancer ou editer le profil `ADAqua.App (MySQL local)` pour tester une connexion MySQL locale depuis Visual Studio.

`ADAqua.Domain` et `ADAqua.Infrastructure` sont des bibliotheques de classes. Elles ne doivent pas etre lancees directement.

## Configuration MySQL locale

La base attendue est `ADAqua`. Le script de creation est disponible ici :

- `SQL/ADAqua_Schema.sql`

L'application lit la chaine de connexion dans la variable d'environnement `ADAQUA_MYSQL_CONNECTION_STRING`.

Exemple de creation durable en variable utilisateur Windows :

```powershell
[Environment]::SetEnvironmentVariable("ADAQUA_MYSQL_CONNECTION_STRING", "Server=localhost;Database=ADAqua;Uid=root;Pwd=motdepasse;", "User")
```

Verification :

```powershell
[Environment]::GetEnvironmentVariable("ADAQUA_MYSQL_CONNECTION_STRING", "User")
```

Apres creation ou modification de cette variable, fermer completement Visual Studio puis le rouvrir. Visual Studio ne voit pas toujours les nouvelles variables creees pendant qu'il est deja ouvert.

## Profil Visual Studio avec MySQL

Le profil `ADAqua.App (MySQL local)` contient une valeur exemple :

```text
Server=localhost;Database=ADAqua;Uid=root;Pwd=CHANGE_ME;
```

Remplacer `CHANGE_ME` localement avant de l'utiliser. Ne pas commiter de vrai mot de passe dans le depot.

## Verification rapide

```powershell
dotnet build .\ADAqua.sln
```
