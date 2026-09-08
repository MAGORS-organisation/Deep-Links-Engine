import type { OgMeta } from '../../api';
import { hostOf } from '../../domain/format';
import { useT } from '../../i18n';
import styles from './OgPreview.module.css';

export function OgPreview({ og, url }: { og: OgMeta; url: string }) {
  const t = useT();
  const image = og.image_url?.trim();
  return (
    <div className={styles.card} aria-label={t('editor.og.preview')}>
      <div className={styles.image}>
        {image ? (
          <img src={image} alt="" onError={(e) => (e.currentTarget.style.visibility = 'hidden')} />
        ) : (
          <span className={styles.noImage}>{t('editor.og.noImage')}</span>
        )}
      </div>
      <div className={styles.body}>
        <span className={styles.site}>{og.site_name?.trim() || hostOf(url) || '—'}</span>
        <span className={styles.title}>{og.title?.trim() || '—'}</span>
        <span className={styles.desc}>{og.description?.trim() || ''}</span>
      </div>
    </div>
  );
}
