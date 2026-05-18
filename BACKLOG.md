# Backlog ADAqua / AquariumTracker

## Termine

- [x] AQ25 - P1 - Corriger l'impossibilite de modifier ou supprimer une mesure des parametres d'eau depuis l'interface.
- [x] AQ01 - P1 - Creer la solution Visual Studio `ADAqua` dans le repertoire `AquariumTracker`.
- [x] AQ02 - P1 - Structurer la solution en trois projets : `ADAqua.App` pour l'application WPF, `ADAqua.Domain` pour le modele metier et `ADAqua.Infrastructure` pour la persistance.
- [x] AQ03 - P1 - Ajouter le modele initial des aquariums : parametres d'eau, plantes et population.
- [x] AQ04 - P1 - Ajouter la dependance `MySqlConnector` et un premier depot MySQL transactionnel.
- [x] AQ05 - P1 - Ajouter un script SQL de creation du schema MySQL `ADAqua`.
- [x] AQ06 - P2 - Ajouter une premiere interface WPF permettant de creer un aquarium, saisir ses mesures d'eau, ses plantes et sa population.
- [x] AQ07 - P2 - Permettre le lancement de l'application sans MySQL configure, avec activation MySQL via `ADAQUA_MYSQL_CONNECTION_STRING`.
- [x] AQ08 - P2 - Corriger la solution pour que `ADAqua.App` soit le projet de demarrage naturel dans Visual Studio.
- [x] AQ09 - P1 - Stabiliser le socle de lancement Visual Studio : verifier le projet de demarrage, ajouter un profil de lancement clair et documenter la configuration locale MySQL.
- [x] AQ10 - P1 - Ajouter un vrai ecran de configuration de la connexion MySQL avec test de connexion, sauvegarde locale securisee et messages utilisateur explicites.
- [x] AQ11 - P1 - Remplacer le stockage en remplacement complet des collections enfants par des operations plus fines : ajout, modification, suppression et conservation de l'historique des mesures.
- [x] AQ12 - P1 - Ajouter les operations CRUD completes pour les aquariums, les plantes et la population, avec suppression confirmee et gestion des erreurs.

## A faire

- [ ] AQ13 - P1 - Ajouter la validation des mesures d'eau : bornes acceptables pour amoniac, nitrites, nitrates, pH, GH, KH et temperature, avec alertes visuelles.
- [ ] AQ14 - P1 - Ajouter un tableau de bord de sante par aquarium : dernieres mesures, tendances, alertes critiques et rappel des actions conseillees.
- [ ] AQ15 - P2 - Ajouter une table d'historique des interventions : changement d'eau, fertilisation, nettoyage filtre, ajout/retrait population, traitement medical.
- [ ] AQ16 - P2 - Ajouter des fiches especes pour la population avec besoins de pH, GH, temperature, volume minimum, comportement et compatibilites.
- [ ] AQ17 - P2 - Ajouter des fiches plantes avec besoins lumiere, CO2, fertilisation, croissance et emplacement conseille.
- [ ] AQ26 - P2 - Creer un referentiel de plantes d'eau douce a partir de sources internet : noms usuels, noms scientifiques et fourchettes de parametres d'eau permettant leur developpement sans probleme.
- [ ] AQ27 - P2 - Creer un referentiel de poissons d'eau douce a partir de sources internet : noms usuels, noms scientifiques et fourchettes de parametres d'eau permettant leur developpement sans probleme.
- [ ] AQ18 - P2 - Ajouter des graphiques d'evolution des parametres d'eau par aquarium et par periode.
- [ ] AQ19 - P2 - Mettre en place des migrations SQL versionnees afin de faire evoluer la base sans recreer toutes les tables.
- [ ] AQ20 - P2 - Ajouter une couche de resilience locale : file d'attente ou cache local lorsque MySQL est indisponible, synchronisation au retour de la connexion.
- [ ] AQ21 - P2 - Ajouter des tests unitaires sur le domaine : validation des mesures, population, compatibilites et calculs d'alertes.
- [ ] AQ22 - P3 - Ameliorer l'ergonomie WPF : edition en ligne, filtres, recherche, meilleure densite d'affichage et raccourcis de saisie.
- [ ] AQ23 - P3 - Ajouter une section `Apparence` : theme clair/sombre, taille de police, densite compacte/confortable et couleur d'accentuation.
- [ ] AQ24 - P3 - Etudier l'import/export CSV ou Excel des mesures d'eau pour faciliter la reprise de donnees existantes.
