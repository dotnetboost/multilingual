import type { Messages } from './en'
import { plural } from './types'

const fr: Messages = {
  app: {
    title: 'DotNetBoost.MultiLingual — Tableau de bord',
    description: 'Gérez les langues et traduisez le contenu via l\'API REST DotNetBoost.MultiLingual.'
  },

  common: {
    refresh: 'Actualiser',
    cancel: 'Annuler',
    save: 'Enregistrer',
    reset: 'Rétablir',
    tryAgain: 'Réessayer',
    none: 'Aucune',
    required: 'Obligatoire.',
    maxLength: '{max} caractères au maximum.',
    apiUnreachable: 'L\'API est injoignable.'
  },

  nav: {
    overview: 'Vue d\'ensemble',
    languages: 'Langues',
    content: 'Contenu',
    apiReference: 'Référence de l\'API',
    documentation: 'Documentation',
    goTo: 'Aller à'
  },

  header: {
    language: 'Langue du tableau de bord'
  },

  appearance: {
    label: 'Apparence',
    theme: 'Thème',
    primary: 'Principale',
    neutral: 'Neutre',
    light: 'Clair',
    dark: 'Sombre',
    uiDocs: 'Documentation Nuxt UI',
    github: 'Dépôt GitHub'
  },

  overview: {
    connecting: 'Connexion…',
    connected: 'API connectée',
    unreachable: 'API injoignable',
    introTitle: 'Du contenu dans toutes les langues, avec un seul attribut',
    introDescription: 'Ce tableau de bord gère les langues que DotNetBoost.MultiLingual.API expose avec MapLanguageEndpoints(), et modifie les propriétés [MultiLingual] des entités de l\'exemple une langue à la fois — chaque écriture part avec cette langue dans Accept-Language.',
    openApiReference: 'Ouvrir la référence de l\'API',
    servedIn: 'Servi en',
    items: plural({ one: '{count} élément', many: '{count} d\'éléments', other: '{count} éléments' }),
    stats: {
      languages: 'Langues',
      activeCount: '{count} actives',
      totalCount: '{count} au total',
      defaultLanguage: 'Langue par défaut',
      defaultDetail: '{code} — les lectures s\'y replient',
      noneConfigured: 'Aucune configurée',
      storageMode: 'Mode de stockage',
      baseLanguage: 'Langue de base : {name}',
      translationsTable: 'Table Translations',
      baseDetail: '{code} est stockée dans les colonnes de chaque entité ; les autres langues dans Translations',
      tableDetail: 'Toutes les langues sont stockées dans la table partagée Translations'
    }
  },

  languages: {
    add: 'Ajouter une langue',
    cardTitle: 'Langues dans lesquelles le contenu est servi',
    cardDescription: 'Une seule est la langue par défaut : les lectures s\'y replient quand la langue demandée n\'a pas de traduction. Le code est l\'identité d\'une langue et ne change jamais ; son contenu est conservé tant qu\'elle est inactive.',
    loadError: 'Impossible de charger les langues',
    empty: 'Aucune langue pour l\'instant. La première ajoutée devient la langue par défaut.',
    columns: {
      code: 'Code',
      name: 'Nom',
      direction: 'Sens',
      role: 'Rôle',
      active: 'Active'
    },
    roles: {
      default: 'par défaut',
      base: 'base'
    },
    baseTooltip: 'MultiLingualOptions.BaseLanguage : stockée dans les colonnes de chaque entité',
    activate: 'Activer {name}',
    deactivate: 'Désactiver {name}',
    actionsFor: 'Actions pour {name}',
    actions: {
      edit: 'Modifier',
      makeDefault: 'Définir par défaut',
      purge: 'Supprimer toutes les traductions',
      delete: 'Supprimer la langue'
    },
    confirmPurge: {
      title: 'Supprimer toutes les traductions en {name} ?',
      description: 'Supprime toutes les traductions écrites en {code}, dans toutes les entités. Les lectures dans cette langue se replient alors sur la langue par défaut. La langue elle-même est conservée.',
      baseNote: 'C\'est la langue de base : ses valeurs sont des colonnes et ne sont pas touchées.',
      label: 'Supprimer les traductions'
    },
    confirmDelete: {
      title: 'Supprimer {name} ?',
      description: 'L\'API refuse tant qu\'il existe des traductions dans cette langue — supprimez-les d\'abord, ou désactivez la langue pour les conserver.',
      label: 'Supprimer la langue'
    },
    form: {
      addTitle: 'Ajouter une langue',
      editTitle: 'Modifier {name}',
      addDescription: 'On peut y écrire du contenu dès sa création ; les lectures se replient tant que rien n\'est écrit.',
      editDescription: 'Remplace entièrement le nom, le sens et l\'état actif. Le code ne peut pas changer.',
      codeDescription: 'ISO 639-1, éventuellement avec une région. C\'est l\'identité de la langue, elle ne change jamais.',
      codeRequired: 'Obligatoire, par ex. en, ar ou pt-BR.',
      nameDescription: 'En général dans la langue elle-même, comme l\'affiche un sélecteur de langue : Español, العربية.',
      directionAddDescription: 'Déduit des données de culture de l\'environnement s\'il n\'est pas choisi : ar, he, fa et ur sont rtl.',
      directionEditDescription: 'Choisir « Déduire » le recalcule à partir du code.',
      directions: {
        auto: 'Déduire du code',
        ltr: 'De gauche à droite (ltr)',
        rtl: 'De droite à gauche (rtl)'
      },
      activeDescription: 'Une langue inactive conserve son contenu mais n\'est jamais servie.',
      makeDefault: 'En faire la langue par défaut',
      firstIsDefault: 'La première langue est toujours la langue par défaut.',
      demotesDefault: 'Remplace la langue par défaut actuelle.'
    },
    toasts: {
      refused: 'L\'API a refusé la modification',
      added: '{name} ajoutée',
      addedDefault: '{code} est la langue par défaut.',
      addedWritable: 'Du contenu peut maintenant être écrit en {code}.',
      saved: '{name} enregistrée',
      inactive: 'Inactive : son contenu est conservé, mais jamais servi.',
      promoted: '{name} est maintenant la langue par défaut',
      promotedDescription: 'Les lectures sans traduction dans la langue demandée s\'y replient.',
      deleted: '{code} supprimée',
      purged: plural({ one: '{count} traduction supprimée', many: '{count} de traductions supprimées', other: '{count} traductions supprimées' }),
      purgedDescription: 'Les lectures en {code} se replient désormais jusqu\'à ce qu\'elle soit de nouveau écrite.'
    }
  },

  content: {
    selectRow: 'Sélectionnez une ligne pour la modifier.',
    resolvedTo: 'résolu en',
    translatedInto: 'Traduit en',
    coverageStored: '{name} : stockée',
    coverageFallsBack: '{name} : se replie',
    deleteAria: 'Supprimer {id}',
    deleteDescription: 'Il est supprimé dans toutes les langues, avec toutes ses traductions.',
    deleteFailed: 'Échec de la suppression',
    editorDescription: 'Modifié une langue à la fois : PUT api/{route}/{id} avec cette langue dans Accept-Language.',
    generic: {
      new: 'Nouveau : {name}',
      create: 'Créer : {name}',
      created: '{name} : créé',
      deleted: '{name} : supprimé',
      deleteTitle: 'Supprimer {name} {id} ?',
      deleteLabel: 'Supprimer : {name}',
      empty: 'Aucun élément pour l\'instant.',
      loadError: 'Impossible de charger : {label}',
      itemLoadError: 'Impossible de charger : {name}',
      itemTitle: '{name} {id}'
    }
  },

  create: {
    description: 'POST api/{route}. Les champs traduisibles sont écrits dans une seule langue ; ajoutez les autres ensuite depuis l\'éditeur.',
    writtenIn: 'Écrit en',
    baseDescription: 'La langue de base. L\'API ne crée que dans celle-ci — toutes les autres langues dépendent de la valeur de base.',
    acceptDescription: 'Envoyée dans Accept-Language. Les lectures dans les autres langues se replient sur celle-ci tant qu\'elles ne sont pas écrites.'
  },

  editor: {
    editingIn: 'Modification en',
    status: {
      base: 'colonne de base',
      translated: 'traduite',
      fallsBack: 'se replie'
    },
    detailsSeparator: 'Détails — identiques dans toutes les langues',
    translatedSeparator: 'Traduit — {name}',
    notStored: 'non stocké',
    referenceLocked: 'Modifiable, mais pas supprimable',
    clear: 'Effacer la traduction',
    saveIn: 'Enregistrer en {name}',
    languagesTitle: 'Langues',
    languagesDescription: 'Choisissez la langue dans laquelle modifier les champs traduits. Les modifications non enregistrées de chacune sont conservées jusqu\'à ce que vous les enregistriez ou les rétablissiez.',
    unsaved: 'non enregistré',
    editing: 'En cours',
    editIn: 'Modifier en {name}',
    detailsPending: 'Les modifications de détails non enregistrées partent avec la prochaine langue que vous enregistrez.',
    footnote: 'Les textes indicatifs montrent sur quoi une lecture se replie tant qu\'une langue n\'a rien de stocké. Les langues inactives ne sont pas listées : rien n\'y est servi.',
    confirmClear: {
      title: 'Effacer la traduction en {code} ?',
      description: 'Tous les champs traduits de cet élément dans cette langue sont supprimés. Les lectures s\'y replient alors, comme pour une langue jamais écrite.',
      label: 'Effacer la traduction'
    },
    toasts: {
      saved: 'Enregistré en {code}',
      saveFailed: 'Échec de l\'enregistrement en {code}',
      cleared: 'Traduction en {code} effacée',
      clearFailed: 'Échec de l\'effacement en {code}'
    }
  },

  error: {
    title: 'Page introuvable',
    description: 'Désolé, cette page est introuvable.'
  },

  types: {
    products: {
      label: 'Produits',
      name: 'Produit',
      description: '[MultiLingual] au niveau des propriétés : Name et Description sont traduits, Sku et Price sont des colonnes ordinaires.',
      fields: { name: 'Nom', description: 'Description' },
      details: {
        sku: { label: 'Référence (SKU)', description: 'Unique, 64 caractères au maximum. Identique dans toutes les langues.' },
        price: { label: 'Prix' },
        categoryId: { label: 'Catégorie' }
      },
      new: 'Nouveau produit',
      create: 'Créer le produit',
      created: 'Produit créé',
      deleted: 'Produit supprimé',
      deleteTitle: 'Supprimer le produit {id} ?',
      deleteLabel: 'Supprimer le produit',
      empty: 'Aucun produit pour l\'instant.',
      loadError: 'Impossible de charger les produits',
      itemLoadError: 'Impossible de charger ce produit',
      itemTitle: 'Produit {id}'
    },
    categories: {
      label: 'Catégories',
      name: 'Catégorie',
      description: '[MultiLingual] au niveau de la classe : toutes les propriétés texte sont traduites, sauf Slug, exclue avec [NotMultiLingual].',
      deleteNote: 'Ses produits sont conservés, sans catégorie.',
      fields: { name: 'Nom', description: 'Description' },
      details: {
        slug: { label: 'Slug', description: 'Unique, 128 caractères au maximum. Exclu avec [NotMultiLingual], il est donc identique dans toutes les langues.' },
        displayOrder: { label: 'Ordre d\'affichage' }
      },
      new: 'Nouvelle catégorie',
      create: 'Créer la catégorie',
      created: 'Catégorie créée',
      deleted: 'Catégorie supprimée',
      deleteTitle: 'Supprimer la catégorie {id} ?',
      deleteLabel: 'Supprimer la catégorie',
      empty: 'Aucune catégorie pour l\'instant.',
      loadError: 'Impossible de charger les catégories',
      itemLoadError: 'Impossible de charger cette catégorie',
      itemTitle: 'Catégorie {id}'
    }
  }
}

export default fr
