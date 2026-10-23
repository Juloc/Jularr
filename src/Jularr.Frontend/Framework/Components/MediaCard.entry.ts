import { onPage } from '../../Modules/Navigation/PageSwap';
import { initializeCards } from './MediaCard';

onPage(import.meta.url, initializeCards);
