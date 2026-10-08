import { Button } from '@/components/ui/button'
import type { LandingContent } from '@/lib/publicApi'
import { cn } from '@/lib/utils'
import { FaqList, iconFor } from './parts'

/** Who the organization is and why to learn with it: its own words from the landing page editor, gathered on one page. */
export default function AboutPage({ name, content, onLink }: { name: string; content: LandingContent; onLink: (link: string) => void }) {
  return (
    <main className="mx-auto flex max-w-6xl flex-col gap-12 px-4 py-10">
      <nav aria-label="Breadcrumb" className="text-sm text-muted-foreground"><a className="hover:underline" href="#/">Home</a> / <span aria-current="page">About</span></nav>
      <header className="flex max-w-3xl flex-col gap-3">
        <h1 className="text-3xl font-bold tracking-tight">About {name}</h1>
        {content.footerAbout ? <p className="text-lg text-muted-foreground">{content.footerAbout}</p> : null}
        <div className="flex flex-wrap gap-3 pt-2">
          <Button onClick={() => onLink('#/courses')}>Browse courses</Button>
          <Button variant="outline" onClick={() => onLink('#login')}>Log in</Button>
        </div>
      </header>

      {content.features.length > 0 ? (
        <section aria-label={content.featuresTitle || 'Why learn with us'} className="flex flex-col gap-4">
          <h2 className="text-xl font-semibold">{content.featuresTitle || 'Why learn with us'}</h2>
          <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
            {content.features.map((feature) => { const Icon = iconFor(feature.icon); return (
              <div key={feature.title} className="flex flex-col gap-2 rounded-xl border border-border bg-card p-5">
                <span className="flex h-10 w-10 items-center justify-center rounded-lg bg-primary/10 text-primary"><Icon className="h-5 w-5" aria-hidden /></span>
                <strong>{feature.title}</strong><p className="text-sm text-muted-foreground">{feature.text}</p>
              </div>
            ) })}
          </div>
        </section>
      ) : null}

      {content.stats.length > 0 ? (
        <section aria-label="In numbers" className={cn('grid gap-4 rounded-2xl bg-primary p-8 text-primary-foreground', content.stats.length > 1 ? 'sm:grid-cols-2 lg:grid-cols-4' : '')}>
          {content.stats.map((stat) => <div key={stat.label} className="text-center"><div className="text-4xl font-bold">{stat.value}</div><div className="mt-1 text-sm opacity-90">{stat.label}</div></div>)}
        </section>
      ) : null}

      {content.testimonials.length > 0 ? (
        <section aria-label={content.testimonialsTitle || 'What learners say'} className="flex flex-col gap-4">
          <h2 className="text-xl font-semibold">{content.testimonialsTitle || 'What learners say'}</h2>
          <div className="grid gap-4 md:grid-cols-2">
            {content.testimonials.map((item) => <figure key={item.name + item.quote} className="flex flex-col gap-3 rounded-xl border border-border bg-card p-5"><blockquote className="text-sm">“{item.quote}”</blockquote><figcaption className="text-sm"><strong>{item.name}</strong>{item.role ? <span className="text-muted-foreground"> · {item.role}</span> : null}</figcaption></figure>)}
          </div>
        </section>
      ) : null}

      {content.faq.length > 0 ? (
        <section aria-label={content.faqTitle || 'Frequently asked questions'} className="flex flex-col gap-4">
          <h2 className="text-center text-xl font-semibold">{content.faqTitle || 'Frequently asked questions'}</h2>
          <FaqList items={content.faq} />
        </section>
      ) : null}
    </main>
  )
}
