import Quality from 'Quality/Quality';
import { QualityProfileFormatItem } from './CustomFormat';

export interface QualityProfileQualityItem {
  id?: number;
  quality?: Quality;
  items: QualityProfileQualityItem[];
  allowed: boolean;
  name?: string;
}

export interface ProfileSizeDefinition {
  qualityId: number;
  minSize: number | null;
  preferredSize: number | null;
  maxSize: number | null;
}

interface QualityProfile {
  isComposite?: boolean;
  compositeVersions?: {
    id: number;
    qualityProfileId: number;
    label: string;
    priority: number;
    isPrimary: boolean;
  }[];
  compositeRootFolder?: string;
  movieCount?: number;
  name: string;
  upgradeAllowed: boolean;
  cutoff: number;
  items: QualityProfileQualityItem[];
  minFormatScore: number;
  cutoffFormatScore: number;
  minUpgradeFormatScore: number;
  formatItems: QualityProfileFormatItem[];
  id: number;
  sizeDefinitions?: ProfileSizeDefinition[] | null;
}

export default QualityProfile;
