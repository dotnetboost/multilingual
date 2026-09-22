import type { Messages } from './en'
import { plural } from './types'

const ar: Messages = {
  app: {
    title: 'DotNetBoost.MultiLingual — لوحة التحكم',
    description: 'إدارة اللغات وترجمة المحتوى عبر واجهة DotNetBoost.MultiLingual البرمجية.'
  },

  common: {
    refresh: 'تحديث',
    cancel: 'إلغاء',
    save: 'حفظ',
    reset: 'استعادة',
    tryAgain: 'إعادة المحاولة',
    none: 'لا شيء',
    required: 'حقل مطلوب.',
    maxLength: 'بحد أقصى {max} حرفًا.',
    apiUnreachable: 'تعذّر الوصول إلى الواجهة البرمجية.'
  },

  nav: {
    overview: 'نظرة عامة',
    languages: 'اللغات',
    content: 'المحتوى',
    apiReference: 'مرجع الواجهة البرمجية',
    documentation: 'التوثيق',
    goTo: 'انتقال إلى'
  },

  header: {
    language: 'لغة لوحة التحكم'
  },

  appearance: {
    label: 'المظهر',
    theme: 'السمة',
    primary: 'اللون الأساسي',
    neutral: 'اللون المحايد',
    light: 'فاتح',
    dark: 'داكن',
    uiDocs: 'توثيق Nuxt UI',
    github: 'مستودع GitHub'
  },

  overview: {
    connecting: 'جارٍ الاتصال…',
    connected: 'الواجهة البرمجية متصلة',
    unreachable: 'الواجهة البرمجية غير متاحة',
    introTitle: 'محتوى بكل اللغات، من سمة واحدة',
    introDescription: 'تدير لوحة التحكم هذه اللغات التي تعرّفها DotNetBoost.MultiLingual.API عبر MapLanguageEndpoints()، وتحرّر خصائص [MultiLingual] في كيانات التطبيق التجريبي لغةً تلو الأخرى — ويُرسَل كل حفظ مع تلك اللغة في Accept-Language.',
    openApiReference: 'فتح مرجع الواجهة البرمجية',
    servedIn: 'متاح بـ',
    items: plural({
      zero: 'لا عناصر',
      one: 'عنصر واحد',
      two: 'عنصران',
      few: '{count} عناصر',
      many: '{count} عنصرًا',
      other: '{count} عنصر'
    }),
    stats: {
      languages: 'اللغات',
      activeCount: '{count} مفعّلة',
      totalCount: '{count} إجمالًا',
      defaultLanguage: 'اللغة الافتراضية',
      defaultDetail: '{code} — يُرجَع إليها عند غياب الترجمة',
      noneConfigured: 'لم تُحدَّد',
      storageMode: 'طريقة التخزين',
      baseLanguage: 'اللغة الأساسية: {name}',
      translationsTable: 'جدول الترجمات',
      baseDetail: '{code} تُخزَّن في أعمدة كل كيان، وبقية اللغات في جدول Translations',
      tableDetail: 'كل اللغات تُخزَّن في جدول Translations المشترك'
    }
  },

  languages: {
    add: 'إضافة لغة',
    cardTitle: 'اللغات التي يُقدَّم بها المحتوى',
    cardDescription: 'لغة واحدة فقط هي الافتراضية: يُرجَع إليها حين لا تتوفر ترجمة باللغة المطلوبة. الرمز هو هوية اللغة ولا يتغيّر أبدًا، ويُحتفَظ بمحتواها أثناء تعطيلها.',
    loadError: 'تعذّر تحميل اللغات',
    empty: 'لا توجد لغات بعد. أول لغة تُضاف تصبح الافتراضية.',
    columns: {
      code: 'الرمز',
      name: 'الاسم',
      direction: 'الاتجاه',
      role: 'الدور',
      active: 'مفعّلة'
    },
    roles: {
      default: 'افتراضية',
      base: 'أساسية'
    },
    baseTooltip: 'MultiLingualOptions.BaseLanguage: تُخزَّن في أعمدة كل كيان',
    activate: 'تفعيل {name}',
    deactivate: 'تعطيل {name}',
    actionsFor: 'إجراءات {name}',
    actions: {
      edit: 'تعديل',
      makeDefault: 'تعيين كافتراضية',
      purge: 'حذف كل الترجمات',
      delete: 'حذف اللغة'
    },
    confirmPurge: {
      title: 'حذف كل ترجمات {name}؟',
      description: 'تُحذف كل ترجمة مكتوبة بـ {code} في جميع الكيانات، ثم يُرجَع إلى اللغة الافتراضية عند القراءة بها. تبقى اللغة نفسها.',
      baseNote: 'إنها اللغة الأساسية، فقيمها أعمدة ولن تتأثر.',
      label: 'حذف الترجمات'
    },
    confirmDelete: {
      title: 'حذف {name}؟',
      description: 'ترفض الواجهة البرمجية الحذف ما دامت هناك ترجمات بها — احذفها أولًا، أو عطّل اللغة للاحتفاظ بها.',
      label: 'حذف اللغة'
    },
    form: {
      addTitle: 'إضافة لغة',
      editTitle: 'تعديل {name}',
      addDescription: 'يمكن الكتابة بها فور إنشائها، ويُرجَع إلى الافتراضية عند القراءة حتى تُكتَب.',
      editDescription: 'استبدال كامل للاسم والاتجاه وحالة التفعيل. لا يمكن تغيير الرمز.',
      codeDescription: 'وفق ISO 639-1، مع المنطقة اختياريًا. إنه هوية اللغة ولا يتغيّر أبدًا.',
      codeRequired: 'حقل مطلوب، مثل en أو ar أو pt-BR.',
      nameDescription: 'عادةً باللغة نفسها، كما يعرضها منتقي اللغات: Español، العربية.',
      directionAddDescription: 'يُستنتَج من بيانات الثقافة في بيئة التشغيل إن تُرك: ar وhe وfa وur من اليمين إلى اليسار.',
      directionEditDescription: 'اختيار «استنتاج» يعيد استنتاجه من الرمز.',
      directions: {
        auto: 'استنتاج من الرمز',
        ltr: 'من اليسار إلى اليمين (ltr)',
        rtl: 'من اليمين إلى اليسار (rtl)'
      },
      activeDescription: 'اللغة المعطّلة تحتفظ بمحتواها لكنها لا تُقدَّم أبدًا.',
      makeDefault: 'تعيينها كافتراضية',
      firstIsDefault: 'أول لغة هي الافتراضية دائمًا.',
      demotesDefault: 'تحلّ محل الافتراضية الحالية.'
    },
    toasts: {
      refused: 'رفضت الواجهة البرمجية التغيير',
      added: 'أُضيفت {name}',
      addedDefault: '{code} هي اللغة الافتراضية.',
      addedWritable: 'يمكن الآن كتابة المحتوى بـ {code}.',
      saved: 'حُفظت {name}',
      inactive: 'معطّلة: يُحتفَظ بمحتواها لكنه لا يُقدَّم أبدًا.',
      promoted: '{name} هي الافتراضية الآن',
      promotedDescription: 'يُرجَع إليها عند القراءة بلغة لا تتوفر بها ترجمة.',
      deleted: 'حُذفت {code}',
      purged: plural({
        zero: 'لم تُحذف أي ترجمة',
        one: 'حُذفت ترجمة واحدة',
        two: 'حُذفت ترجمتان',
        few: 'حُذفت {count} ترجمات',
        many: 'حُذفت {count} ترجمة',
        other: 'حُذفت {count} ترجمة'
      }),
      purgedDescription: 'القراءة بـ {code} تُرجَع الآن إلى الافتراضية حتى يُكتب بها من جديد.'
    }
  },

  content: {
    selectRow: 'اختر صفًا لتحريره.',
    resolvedTo: 'قُدِّم بـ',
    translatedInto: 'مترجم إلى',
    coverageStored: '{name}: مخزّنة',
    coverageFallsBack: '{name}: يُرجَع إلى الافتراضية',
    deleteAria: 'حذف {id}',
    deleteDescription: 'يُحذف من كل اللغات مع جميع ترجماته.',
    deleteFailed: 'تعذّر الحذف',
    editorDescription: 'يُحرَّر لغةً تلو الأخرى: PUT api/{route}/{id} مع تلك اللغة في Accept-Language.',
    generic: {
      new: '{name} جديد',
      create: 'إنشاء {name}',
      created: 'أُنشئ {name}',
      deleted: 'حُذف {name}',
      deleteTitle: 'حذف {name} {id}؟',
      deleteLabel: 'حذف {name}',
      empty: 'لا يوجد {label} بعد.',
      loadError: 'تعذّر تحميل {label}',
      itemLoadError: 'تعذّر تحميل {name}',
      itemTitle: '{name} {id}'
    }
  },

  create: {
    description: 'POST api/{route}. تُكتب الحقول القابلة للترجمة بلغة واحدة، وتُضاف بقية اللغات لاحقًا من المحرّر.',
    writtenIn: 'مكتوب بـ',
    baseDescription: 'اللغة الأساسية. لا تُنشئ الواجهة البرمجية إلا بها — فكل لغة أخرى تتبع القيمة الأساسية.',
    acceptDescription: 'تُرسَل في Accept-Language. القراءة ببقية اللغات تُرجَع إلى هذه اللغة حتى تُكتب.'
  },

  editor: {
    editingIn: 'التحرير بـ',
    status: {
      base: 'عمود أساسي',
      translated: 'مترجمة',
      fallsBack: 'يُرجَع إلى الافتراضية'
    },
    detailsSeparator: 'التفاصيل — واحدة في كل اللغات',
    translatedSeparator: 'مترجم — {name}',
    notStored: 'غير مخزّن',
    referenceLocked: 'يمكن تغييره لا إزالته',
    clear: 'مسح الترجمة',
    saveIn: 'حفظ بـ {name}',
    languagesTitle: 'اللغات',
    languagesDescription: 'اختر اللغة التي تحرّر بها الحقول المترجمة. يُحتفَظ بالتعديلات غير المحفوظة في كل لغة حتى تحفظها أو تستعيد الأصل.',
    unsaved: 'غير محفوظ',
    editing: 'قيد التحرير',
    editIn: 'تحرير بـ {name}',
    detailsPending: 'تُحفَظ تعديلات التفاصيل مع أول لغة تحفظها بعدها.',
    footnote: 'تعرض النصوص الإرشادية ما تُرجَع إليه القراءة حين لا يُخزَّن شيء في لغة ما. اللغات المعطّلة غير مدرجة: لا يُقدَّم بها شيء.',
    confirmClear: {
      title: 'مسح ترجمة {code}؟',
      description: 'تُحذف كل الحقول المترجمة لهذا العنصر بتلك اللغة، ثم يُرجَع إلى الافتراضية عند القراءة بها كأنها لم تُكتب قط.',
      label: 'مسح الترجمة'
    },
    toasts: {
      saved: 'حُفظ بـ {code}',
      saveFailed: 'تعذّر الحفظ بـ {code}',
      cleared: 'مُسحت ترجمة {code}',
      clearFailed: 'تعذّر مسح {code}'
    }
  },

  error: {
    title: 'الصفحة غير موجودة',
    description: 'عذرًا، تعذّر العثور على هذه الصفحة.'
  },

  types: {
    products: {
      label: 'المنتجات',
      name: 'منتج',
      description: '[MultiLingual] على مستوى الخاصية: يُترجَم Name وDescription، أما Sku وPrice فأعمدة عادية.',
      fields: { name: 'الاسم', description: 'الوصف' },
      details: {
        sku: { label: 'رمز المنتج (SKU)', description: 'فريد، بحد أقصى 64 حرفًا. واحد في كل اللغات.' },
        price: { label: 'السعر' },
        categoryId: { label: 'الفئة' }
      },
      new: 'منتج جديد',
      create: 'إنشاء المنتج',
      created: 'أُنشئ المنتج',
      deleted: 'حُذف المنتج',
      deleteTitle: 'حذف المنتج {id}؟',
      deleteLabel: 'حذف المنتج',
      empty: 'لا توجد منتجات بعد.',
      loadError: 'تعذّر تحميل المنتجات',
      itemLoadError: 'تعذّر تحميل هذا المنتج',
      itemTitle: 'المنتج {id}'
    },
    categories: {
      label: 'الفئات',
      name: 'فئة',
      description: '[MultiLingual] على مستوى الصنف: تُترجَم كل الخصائص النصية عدا Slug، المستثناة بـ [NotMultiLingual].',
      deleteNote: 'تبقى منتجاتها، دون فئة.',
      fields: { name: 'الاسم', description: 'الوصف' },
      details: {
        slug: { label: 'المعرّف النصي (Slug)', description: 'فريد، بحد أقصى 128 حرفًا. مستثنى بـ [NotMultiLingual]، فهو واحد في كل اللغات.' },
        displayOrder: { label: 'ترتيب العرض' }
      },
      new: 'فئة جديدة',
      create: 'إنشاء الفئة',
      created: 'أُنشئت الفئة',
      deleted: 'حُذفت الفئة',
      deleteTitle: 'حذف الفئة {id}؟',
      deleteLabel: 'حذف الفئة',
      empty: 'لا توجد فئات بعد.',
      loadError: 'تعذّر تحميل الفئات',
      itemLoadError: 'تعذّر تحميل هذه الفئة',
      itemTitle: 'الفئة {id}'
    }
  }
}

export default ar
