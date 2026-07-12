import { motion, type Variants } from 'framer-motion'
import type { ReactNode } from 'react'
import { cn } from '../lib/utils'

export function Container({ className, children }: { className?: string; children: ReactNode }) {
  return <div className={cn('mx-auto w-full max-w-6xl px-6', className)}>{children}</div>
}

const revealV: Variants = {
  hidden: { opacity: 0, y: 24 },
  show: (i: number = 0) => ({
    opacity: 1,
    y: 0,
    transition: { duration: 0.6, delay: i * 0.08, ease: [0.22, 1, 0.36, 1] },
  }),
}

export function Reveal({
  children,
  i = 0,
  className,
  as = 'div',
}: {
  children: ReactNode
  i?: number
  className?: string
  as?: 'div' | 'section' | 'span'
}) {
  const M = motion[as] as typeof motion.div
  return (
    <M
      className={className}
      custom={i}
      variants={revealV}
      initial="hidden"
      whileInView="show"
      viewport={{ once: true, margin: '-80px' }}
    >
      {children}
    </M>
  )
}

export function Button({
  children,
  variant = 'primary',
  href,
  className,
  onClick,
}: {
  children: ReactNode
  variant?: 'primary' | 'ghost'
  href?: string
  className?: string
  onClick?: () => void
}) {
  const base =
    'group inline-flex items-center justify-center gap-2 rounded-full px-5 py-2.5 text-sm font-semibold transition-all duration-200 focus:outline-none focus-visible:ring-2 focus-visible:ring-brand/60'
  const styles =
    variant === 'primary'
      ? 'bg-brand text-ink hover:bg-brand-2 shadow-[0_8px_30px_-8px_rgba(16,185,129,0.6)] hover:shadow-[0_10px_40px_-6px_rgba(16,185,129,0.8)] hover:-translate-y-0.5'
      : 'border border-line bg-white/[0.02] text-fg hover:bg-white/[0.06] hover:border-white/20'
  const Cmp: any = href ? 'a' : 'button'
  return (
    <Cmp href={href} onClick={onClick} className={cn(base, styles, className)}>
      {children}
    </Cmp>
  )
}

export function Pill({ children, className }: { children: ReactNode; className?: string }) {
  return (
    <span
      className={cn(
        'inline-flex items-center gap-1.5 rounded-full border border-line bg-white/[0.03] px-3 py-1 text-xs font-medium text-fg-dim',
        className,
      )}
    >
      {children}
    </span>
  )
}
