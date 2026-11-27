<?php
/**
 * 「あなたに合った競馬レース診断」（旧: 今日の狙い目レース診断）。
 *
 * 4つの質問に答えると、AI指数ダイジェストの公開データ（レースごとの候補馬・AI指数・要素別評価・
 * 人気）から、まだ発走していないレースを決まったルールで絞り込んで提案する。ランダムは使わない。
 * AI指数ダイジェストの計算・記事の自動生成には手を入れず、保存済みの投稿メタ（races / picks）を読むだけ。
 *
 * LINE連携は horse-race-custom-builder の解放判定（hrc_is_unlocked）を再利用する。詳細（馬名・AI指数など）は
 * 解放済みの場合だけサーバー側のレスポンスに含める（未解放の人にはAPIからも返さない）。
 */

if (!defined('ABSPATH')) {
    exit;
}

define('KEIBA_DIAG_PAGE_SLUG', 'race-diagnosis');
define('KEIBA_DIAG_TITLE', 'あなたに合った競馬レース診断');
define('KEIBA_DIAG_OLD_TITLE', '今日の狙い目レース診断');
define('KEIBA_DIAG_DISCLAIMER', 'AI指数に基づく参考情報であり、的中を保証するものではありません。');
define('KEIBA_DIAG_DB_VERSION', '1');

/** 注目度の基準。AI指数・要素別評価は偏差値（平均50）なので、60以上=S、55以上=A、50以上=Bとする。 */
define('KEIBA_DIAG_RANK_S', 60.0);
define('KEIBA_DIAG_RANK_A', 55.0);
define('KEIBA_DIAG_RANK_B', 50.0);

/* ------------------------------------------------------------------------- *
 * 純粋なロジック（WordPressに依存しない。テストしやすいように分けてある）
 * ------------------------------------------------------------------------- */

function keiba_diag_allowed_answers()
{
    return array(
        's' => array('hit', 'ana', 'high', 'ai'),          // Q1 重視したいこと
        'p' => array('fav', 'mid', 'long', 'any'),         // Q2 狙う馬のタイプ（人気）
        'm' => array('pace', 'agari', 'jockey', 'pedigree', 'ai'), // Q3 重視する予想材料
        'c' => array('central', 'local', 'both'),          // Q4 中央/地方
    );
}

/** 不正な値は受け付けない（nullを返す）。 */
function keiba_diag_sanitize_answers($raw)
{
    $allowed = keiba_diag_allowed_answers();
    $answers = array();
    foreach ($allowed as $key => $values) {
        $value = isset($raw[$key]) ? (string) $raw[$key] : '';
        if (!in_array($value, $values, true)) {
            return null;
        }
        $answers[$key] = $value;
    }
    return $answers;
}

function keiba_diag_is_central($track_code)
{
    return in_array((string) $track_code, array('01', '02', '03', '04', '05', '06', '07', '08', '09', '10'), true);
}

/** 要素別評価（FactorScores）から、質問Q3で選んだ「予想材料」の値を返す。無ければnull。 */
function keiba_diag_material_value($candidate, $material)
{
    $factors = isset($candidate['factors']) && is_array($candidate['factors']) ? $candidate['factors'] : array();
    switch ($material) {
        case 'pace':
            return isset($factors['paramPace']) && is_numeric($factors['paramPace']) ? (float) $factors['paramPace'] : null;
        case 'agari':
            return isset($factors['paramAgariQ']) && is_numeric($factors['paramAgariQ']) ? (float) $factors['paramAgariQ'] : null;
        case 'jockey':
            return isset($factors['paramJockeyRoi']) && is_numeric($factors['paramJockeyRoi']) ? (float) $factors['paramJockeyRoi'] : null;
        case 'pedigree':
            $values = array();
            foreach (array('paramPedigreeFit', 'paramTrainingAcc') as $key) {
                if (isset($factors[$key]) && is_numeric($factors[$key])) {
                    $values[] = (float) $factors[$key];
                }
            }
            return $values ? array_sum($values) / count($values) : null;
        default:
            return isset($candidate['aiIndex']) && is_numeric($candidate['aiIndex']) ? (float) $candidate['aiIndex'] : null;
    }
}

/** 質問Q1（スタイル）とQ2（人気層）を満たす候補馬か。$use_popularityがfalseならQ2を無視する。 */
function keiba_diag_candidate_matches($candidate, $style, $popularity, $use_popularity)
{
    $ninki = isset($candidate['ninki']) && is_numeric($candidate['ninki']) ? (int) $candidate['ninki'] : null;
    $odds = isset($candidate['tanshoOdds']) && is_numeric($candidate['tanshoOdds']) ? (float) $candidate['tanshoOdds'] : null;
    $roles = isset($candidate['roles']) && is_array($candidate['roles']) ? $candidate['roles'] : array();

    // 人気・オッズが取れていない馬（発売前の朝など）は、人気で絞る質問には答えられない。
    // ただし「AIの総合評価を重視」で、人気層の指定が無い（または人気の条件を外した）場合は、
    // 人気を使わないので、AI指数だけで判定できる。
    if ($ninki === null) {
        return $style === 'ai' && (!$use_popularity || $popularity === 'any');
    }

    switch ($style) {
        case 'hit':
            if ($ninki > 5) {
                return false;
            }
            break;
        case 'ana':
            if ($ninki < 5 && !in_array('ana', $roles, true)) {
                return false;
            }
            break;
        case 'high':
            if ($odds === null || $odds < 15.0) {
                return false;
            }
            break;
        default: // ai
            break;
    }

    if ($use_popularity) {
        if ($popularity === 'fav' && $ninki > 3) {
            return false;
        }
        if ($popularity === 'mid' && ($ninki < 4 || $ninki > 7)) {
            return false;
        }
        if ($popularity === 'long' && $ninki < 8) {
            return false;
        }
    }
    return true;
}

/**
 * 1レースの診断スコア。条件に合う候補馬のうち最も高い値を返す。合う馬が無ければnull。
 * スコア = 重視する予想材料の評価 と AI指数 の平均（材料が「AI総合指数」ならAI指数そのもの）。
 */
function keiba_diag_score_race($race, $answers, $use_popularity)
{
    $best = null;
    $candidates = isset($race['candidates']) && is_array($race['candidates']) ? $race['candidates'] : array();
    foreach ($candidates as $candidate) {
        if (!isset($candidate['aiIndex']) || !is_numeric($candidate['aiIndex'])) {
            continue; // AI指数が取得できない馬は推薦しない。
        }
        if (!keiba_diag_candidate_matches($candidate, $answers['s'], $answers['p'], $use_popularity)) {
            continue;
        }
        $material = keiba_diag_material_value($candidate, $answers['m']);
        if ($material === null) {
            continue; // 選んだ予想材料が算出できていない馬は、その材料では評価できない。
        }
        $ai = (float) $candidate['aiIndex'];
        $score = $answers['m'] === 'ai' ? $ai : ($material + $ai) / 2.0;
        if ($best === null || $score > $best['score']) {
            $best = array('score' => $score, 'candidate' => $candidate, 'material' => $material);
        }
    }
    return $best;
}

function keiba_diag_rank($score)
{
    if ($score >= KEIBA_DIAG_RANK_S) {
        return 'S';
    }
    if ($score >= KEIBA_DIAG_RANK_A) {
        return 'A';
    }
    if ($score >= KEIBA_DIAG_RANK_B) {
        return 'B';
    }
    return null;
}

function keiba_diag_style_label($style)
{
    $labels = array('hit' => '的中率重視', 'ana' => '穴馬', 'high' => '高配当', 'ai' => 'AI総合');
    return isset($labels[$style]) ? $labels[$style] : '';
}

function keiba_diag_material_label($material)
{
    $labels = array('pace' => '展開', 'agari' => '末脚', 'jockey' => '騎手・コース', 'pedigree' => '血統・調教', 'ai' => 'AI指数');
    return isset($labels[$material]) ? $labels[$material] : '';
}

/** 「穴馬・展開重視型」のような診断タイプ名。 */
function keiba_diag_type_label($answers)
{
    $style = keiba_diag_style_label($answers['s']);
    if ($answers['s'] === 'ai' && $answers['m'] === 'ai') {
        return 'AI総合型';
    }
    if ($answers['m'] === 'ai') {
        return $style . '・AI指数重視型';
    }
    return $style . '・' . keiba_diag_material_label($answers['m']) . '重視型';
}

/** 選定理由。すべて実データから作る（根拠のない文は出さない）。 */
function keiba_diag_reasons($race, $best, $answers)
{
    $reasons = array();
    $candidate = $best['candidate'];
    $ai = (float) $candidate['aiIndex'];
    $roles = isset($candidate['roles']) && is_array($candidate['roles']) ? $candidate['roles'] : array();

    if ($ai >= 55.0) {
        $reasons[] = 'AI指数が高い馬がいる（' . number_format($ai, 1) . '。平均は50）';
    }

    if (in_array('ana', $roles, true) && isset($candidate['ninki'])) {
        $reasons[] = 'AI評価に対して人気が低い馬がいる（' . (int) $candidate['ninki'] . '番人気）';
    } elseif (isset($candidate['ninki']) && (int) $candidate['ninki'] <= 3 && $answers['s'] === 'hit') {
        $reasons[] = 'AI評価と人気が一致している馬がいる（' . (int) $candidate['ninki'] . '番人気）';
    }

    $material_messages = array(
        'pace' => '展開・先行力の評価が高い馬がいる',
        'agari' => '上がり・末脚の評価が高い馬がいる',
        'jockey' => '騎手・コース適性の評価が高い馬がいる',
        'pedigree' => '血統・調教の評価が高い馬がいる',
    );
    if (isset($material_messages[$answers['m']]) && $best['material'] >= 55.0) {
        $reasons[] = $material_messages[$answers['m']] . '（' . number_format($best['material'], 1) . '）';
    }

    $reasons[] = '回答した予想スタイル（' . keiba_diag_style_label($answers['s']) . '・' . keiba_diag_material_label($answers['m']) . '）と相性が良い';
    return array_slice($reasons, 0, 4);
}

/**
 * 診断の本体。$races は次の形の配列（各開催場の `races` メタをまとめたもの）:
 *   array(array('trackCode', 'venue', 'link', 'raceNumber', 'raceName', 'startTime', 'candidates' => array(...)))
 * 発走済み（発走時刻 <= $now_hhmm）のレース、発走時刻不明のレース、AI指数のある候補が無いレースは除く。
 *
 * @return array array('items' => 上位のレース, 'relaxed' => 人気の条件を外して選んだか)
 */
/**
 * 診断の対象になるレース（中央/地方の条件に合い、発走時刻が分かっていて、まだ発走していない）。
 * $course は central / local / both。ばんえい競馬は診断の対象外。
 */
function keiba_diag_eligible_races($races, $course, $now_hhmm)
{
    $eligible = array();
    foreach ($races as $race) {
        if ($course === 'central' && !keiba_diag_is_central($race['trackCode'])) {
            continue;
        }
        if ($course === 'local' && keiba_diag_is_central($race['trackCode'])) {
            continue;
        }
        if ($race['trackCode'] === '83') {
            continue; // ばんえい競馬は診断の対象外。
        }
        if (empty($race['startTime']) || $race['startTime'] <= $now_hhmm) {
            continue; // 発走時刻が不明、または発走済み。
        }
        $eligible[] = $race;
    }
    return $eligible;
}

function keiba_diag_select($races, $answers, $now_hhmm, $limit = 3)
{
    $eligible = keiba_diag_eligible_races($races, $answers['c'], $now_hhmm);

    $relaxed = false;
    $scored = keiba_diag_score_all($eligible, $answers, true);
    if (!$scored) {
        // 人気の条件まで合うレースが無いときは、人気の条件を外して選び直し、その旨を表示する。
        $scored = keiba_diag_score_all($eligible, $answers, false);
        $relaxed = (bool) $scored;
    }

    usort($scored, function ($a, $b) {
        if ($a['score'] === $b['score']) {
            return strcmp($a['race']['startTime'], $b['race']['startTime']);
        }
        return $a['score'] < $b['score'] ? 1 : -1;
    });

    $items = array();
    foreach (array_slice($scored, 0, $limit) as $row) {
        $items[] = array(
            'race' => $row['race'],
            'best' => $row['best'],
            'score' => $row['score'],
            'rank' => keiba_diag_rank($row['score']),
        );
    }
    return array('items' => $items, 'relaxed' => $relaxed);
}

function keiba_diag_score_all($eligible, $answers, $use_popularity)
{
    $scored = array();
    foreach ($eligible as $race) {
        $best = keiba_diag_score_race($race, $answers, $use_popularity);
        if ($best === null || keiba_diag_rank($best['score']) === null) {
            continue; // 基準（偏差値50）に届かないレースは推薦しない。
        }
        $scored[] = array('race' => $race, 'best' => $best, 'score' => $best['score']);
    }
    return $scored;
}

/**
 * 本日の開催状況（選択画面に表示する）。state は
 *   open=診断できるレースあり / ended=本日の開催は終了 / none=本日の開催なし / preparing=データ準備中
 * remaining は診断できる（まだ発走していない）レース数。
 */
function keiba_diag_status($loaded, $now_hhmm)
{
    $out = array();
    $total = 0;
    foreach (array('central', 'local') as $course) {
        $group = isset($loaded['groups'][$course]) ? $loaded['groups'][$course] : array('opened' => false, 'meta' => false);
        $remaining = count(keiba_diag_eligible_races($loaded['races'], $course, $now_hhmm));
        if ($remaining > 0) {
            $state = 'open';
        } elseif (!$group['opened']) {
            $state = 'none';
        } elseif (!$group['meta']) {
            $state = 'preparing';
        } else {
            $state = 'ended';
        }
        $out[$course] = array('state' => $state, 'remaining' => $remaining);
        $total += $remaining;
    }
    $out['both'] = array('remaining' => $total);
    return $out;
}

/**
 * 診断できるレースが無いときの代替診断。回答（重視すること・狙う馬・予想材料）から、
 * 「あなたに合った競馬スタイル」を決まった文面で返す。レースのデータは使わない（ランダムも使わない）。
 */
function keiba_diag_fallback($answers)
{
    $styles = array(
        'hit' => array('堅実データ派', '的中率を大切にする、堅実なタイプです。人気とAIの評価が一致する馬を軸に、無理のない組み立てを好みます。'),
        'ana' => array('穴馬ハンター', 'AIの評価に対して人気が低い、妙味のある馬を探すタイプです。人気に流されず、自分の目で狙いを定めるのが得意です。'),
        'high' => array('一撃ロマン派', '高配当を狙う、夢のあるタイプです。人気薄の一発に期待し、レースを大きく楽しめます。'),
        'ai' => array('AI分析派', 'AIの総合評価を頼りに、データで冷静に判断するタイプです。数字の裏付けがあるレース選びを好みます。'),
    );
    $materials = array(
        'pace' => '展開・先行力：ペースや位置取りを読んで、レースの流れから狙いを探すのが向いています。',
        'agari' => '上がり・末脚：最後の脚の使い方に注目して、差し・追い込みの馬を探すのが向いています。',
        'jockey' => '騎手・コース適性：騎手とコースの相性を重視して、得意な条件の馬を探すのが向いています。',
        'pedigree' => '血統・調教：血統の適性と調教の動きから、距離や馬場への合い方を探すのが向いています。',
        'ai' => 'AI総合指数：複数の評価をまとめたAI指数を見て、全体のバランスで選ぶのが向いています。',
    );
    $popularity = array(
        'fav' => '人気馬中心：上位人気の中から、評価の高い馬を丁寧に選ぶ見方が合っています。',
        'mid' => '中穴中心：4〜7番人気あたりの、評価の割に配当が見込める馬を狙う見方が合っています。',
        'long' => '大穴中心：8番人気以下で評価の高い馬を探す、思い切った見方が合っています。',
        'any' => '人気にこだわらず、レースごとに最も評価の高い馬を選ぶ柔軟な見方が合っています。',
    );
    $courses = array(
        'central' => '次の中央競馬の開催',
        'local' => '次の地方競馬の開催',
        'both' => '次の中央・地方の開催',
    );

    $style = $styles[$answers['s']];
    return array(
        'styleName' => $style[0],
        'lead' => $style[1],
        'points' => array($materials[$answers['m']], $popularity[$answers['p']]),
        'next' => $courses[$answers['c']] . 'で、この診断をもう一度お試しください。あなたのスタイルに合ったレースをご案内します。',
    );
}

/** 詳細（解放済みの人にだけ返す）。本命馬・注目穴馬・危険な人気馬・AI指数。 */
function keiba_diag_detail($race)
{
    $labels = array('nerai' => '本命馬', 'ana' => '注目穴馬', 'kiken' => '危険な人気馬');
    $rows = array();
    $candidates = isset($race['candidates']) && is_array($race['candidates']) ? $race['candidates'] : array();
    usort($candidates, function ($a, $b) {
        $ai = isset($a['aiIndex']) ? (float) $a['aiIndex'] : 0;
        $bi = isset($b['aiIndex']) ? (float) $b['aiIndex'] : 0;
        return $ai < $bi ? 1 : ($ai > $bi ? -1 : 0);
    });
    foreach ($candidates as $candidate) {
        $roles = isset($candidate['roles']) && is_array($candidate['roles']) ? $candidate['roles'] : array();
        $role_labels = array();
        foreach ($roles as $role) {
            if (isset($labels[$role])) {
                $role_labels[] = $labels[$role];
            }
        }
        $rows[] = array(
            'umaban' => isset($candidate['umaban']) ? (int) $candidate['umaban'] : null,
            'horseName' => isset($candidate['horseName']) ? (string) $candidate['horseName'] : '',
            'aiIndex' => isset($candidate['aiIndex']) ? round((float) $candidate['aiIndex'], 1) : null,
            'ninki' => isset($candidate['ninki']) ? (int) $candidate['ninki'] : null,
            'roles' => $role_labels ? $role_labels : array('AI指数上位'),
        );
    }
    return $rows;
}

/**
 * ダイジェストのデータが新しいか。収集アプリは20分ごとに更新するため、最後の更新から一定時間
 * （既定60分）たったデータは使わない。開催中止や収集アプリの停止で古いデータが残っていても、
 * 終わったレースや中止のレースを推薦しないための安全策。更新時刻が読めない場合は古いものとして扱う。
 */
function keiba_diag_is_fresh($updated_at, $now_timestamp, $max_minutes = 60)
{
    $ts = strtotime((string) $updated_at);
    if ($ts === false || $ts <= 0) {
        return false;
    }
    return ($now_timestamp - $ts) <= $max_minutes * 60;
}

/** 流入元の分類（x / youtube / search / site / direct / other）。UTMがあればそれを優先し、無ければリファラのホスト名で判定する。 */
function keiba_diag_classify_source($utm_source, $referrer_host, $own_host)
{
    $utm = strtolower(trim((string) $utm_source));
    $host = strtolower(trim((string) $referrer_host));
    $key = $utm !== '' ? $utm : $host;

    if ($key === '') {
        return 'direct';
    }
    if ($utm === '' && $own_host !== '' && $host === strtolower($own_host)) {
        return 'site';
    }
    if ($key === 'x' || strpos($key, 'twitter') !== false || strpos($key, 'x.com') !== false || $key === 't.co') {
        return 'x';
    }
    if (strpos($key, 'youtube') !== false || strpos($key, 'youtu.be') !== false) {
        return 'youtube';
    }
    foreach (array('google', 'yahoo', 'bing', 'duckduckgo', 'search') as $needle) {
        if (strpos($key, $needle) !== false) {
            return 'search';
        }
    }
    return 'other';
}

function keiba_diag_events()
{
    return array(
        'view', 'start', 'complete', 'detail_click', 'line_click', 'detail_view',
        // 通常の診断結果と代替診断結果を区別するためのイベント（表示数・LINEボタンのクリック数）。
        'result_normal', 'result_fallback', 'line_click_normal', 'line_click_fallback',
        'recheck',
    );
}

function keiba_diag_sources()
{
    return array('x' => 'X', 'youtube' => 'YouTube', 'search' => '検索', 'site' => '既存サイト', 'direct' => '直接', 'other' => 'その他');
}

/* ------------------------------------------------------------------------- *
 * WordPress側の組み込み
 * ------------------------------------------------------------------------- */

/** 診断ページ（固定ページ）を、無ければ作る。 */
function keiba_ai_digest_ensure_diagnosis_page()
{
    $id = (int) get_option('keiba_ai_digest_diagnosis_page_id', 0);
    if ($id && get_post_status($id) && get_post_status($id) !== 'trash') {
        return $id;
    }

    $existing = get_page_by_path(KEIBA_DIAG_PAGE_SLUG);
    if ($existing) {
        update_option('keiba_ai_digest_diagnosis_page_id', (int) $existing->ID, false);
        return (int) $existing->ID;
    }

    $id = wp_insert_post(array(
        'post_type' => 'page',
        'post_status' => 'publish',
        'post_title' => KEIBA_DIAG_TITLE,
        'post_name' => KEIBA_DIAG_PAGE_SLUG,
        'post_content' => '[keiba_race_diagnosis]',
    ));
    if (!is_wp_error($id) && $id) {
        update_option('keiba_ai_digest_diagnosis_page_id', (int) $id, false);
        return (int) $id;
    }
    return 0;
}

add_action('init', function () {
    if (get_option('keiba_ai_digest_diagnosis_db_version') !== KEIBA_DIAG_DB_VERSION) {
        keiba_diag_install_table();
        update_option('keiba_ai_digest_diagnosis_db_version', KEIBA_DIAG_DB_VERSION, false);
    }
    if (!get_option('keiba_ai_digest_diagnosis_page_id')) {
        keiba_ai_digest_ensure_diagnosis_page();
    }
    keiba_diag_rename_page_once();
});

/** 旧名称のままの診断ページのタイトルを、新しい名称に一度だけ直す（運営者が自分で変えていたら触らない）。 */
function keiba_diag_rename_page_once()
{
    if (get_option('keiba_ai_digest_diagnosis_renamed')) {
        return;
    }
    $id = (int) get_option('keiba_ai_digest_diagnosis_page_id', 0);
    if ($id && get_post_field('post_title', $id) === KEIBA_DIAG_OLD_TITLE) {
        wp_update_post(array('ID' => $id, 'post_title' => KEIBA_DIAG_TITLE));
        if (function_exists('rocket_clean_post')) {
            rocket_clean_post($id); // ページキャッシュに古い名称が残らないように。
        }
    }
    update_option('keiba_ai_digest_diagnosis_renamed', 1, false);
}

function keiba_diag_table()
{
    global $wpdb;
    return $wpdb->prefix . 'keiba_diag_events';
}

function keiba_diag_install_table()
{
    global $wpdb;
    require_once ABSPATH . 'wp-admin/includes/upgrade.php';
    $table = keiba_diag_table();
    $charset = $wpdb->get_charset_collate();
    dbDelta("CREATE TABLE {$table} (
        day date NOT NULL,
        event varchar(20) NOT NULL,
        source varchar(12) NOT NULL,
        total int unsigned NOT NULL DEFAULT 0,
        PRIMARY KEY  (day, event, source)
    ) {$charset};");
}

function keiba_diag_record_event($event, $source)
{
    global $wpdb;
    $table = keiba_diag_table();
    $day = (new DateTimeImmutable('now', new DateTimeZone('Asia/Tokyo')))->format('Y-m-d');
    $wpdb->query($wpdb->prepare(
        "INSERT INTO {$table} (day, event, source, total) VALUES (%s, %s, %s, 1)
         ON DUPLICATE KEY UPDATE total = total + 1",
        $day,
        $event,
        $source
    ));
}

/** 今日（日本時間）の、公開中のダイジェストから、診断に使うレース一覧を集める。 */
function keiba_diag_load_races()
{
    $today = (new DateTimeImmutable('now', new DateTimeZone('Asia/Tokyo')))->format('Y-m-d');
    $posts = get_posts(array(
        'post_type' => 'keiba_digest',
        'post_status' => 'publish',
        'numberposts' => 40,
        'meta_key' => 'race_date',
        'meta_value' => $today,
    ));

    $races = array();
    $has_races_meta = false;
    // 開催状況の表示用。更新が止まっている投稿も「本日の開催はあった」ものとして数える
    // （レース終了後に更新が止まっても「開催なし」と表示しないため）。ばんえい競馬は診断の対象外。
    $groups = array(
        'central' => array('opened' => false, 'meta' => false),
        'local' => array('opened' => false, 'meta' => false),
    );
    foreach ($posts as $post) {
        if (!get_post_meta($post->ID, 'license_visible', true)) {
            continue;
        }
        $group_track = (string) get_post_meta($post->ID, 'track_code', true);
        if ($group_track !== '83') {
            $group_key = keiba_diag_is_central($group_track) ? 'central' : 'local';
            $groups[$group_key]['opened'] = true;
            $group_raw = get_post_meta($post->ID, 'races', true);
            if ($group_raw && is_array(json_decode($group_raw, true))) {
                $groups[$group_key]['meta'] = true;
            }
        }
        if (!keiba_diag_is_fresh(get_post_meta($post->ID, 'updated_at', true), time())) {
            continue; // 更新が止まっている開催場（中止・収集の停止など）は推薦しない。
        }
        $track_code = (string) get_post_meta($post->ID, 'track_code', true);
        $raw = get_post_meta($post->ID, 'races', true);
        $list = $raw ? json_decode($raw, true) : null;
        if (!is_array($list)) {
            continue;
        }
        $has_races_meta = true;

        $venue = '';
        if (preg_match('/^\d{4}\/\d{2}\/\d{2}\s+(\S+)\s/u', $post->post_title, $m)) {
            $venue = $m[1];
        }
        foreach ($list as $race) {
            if (!is_array($race) || empty($race['candidates'])) {
                continue;
            }
            $races[] = array(
                'trackCode' => $track_code,
                'venue' => $venue,
                'link' => get_permalink($post->ID),
                'raceNumber' => (int) ($race['raceNumber'] ?? 0),
                'raceName' => (string) ($race['raceName'] ?? ''),
                'startTime' => (string) ($race['startTime'] ?? ''),
                'candidates' => $race['candidates'],
            );
        }
    }
    return array('races' => $races, 'prepared' => $has_races_meta, 'groups' => $groups);
}

add_action('rest_api_init', function () {
    register_rest_route('keiba-ai/v1', '/diagnosis', array(
        'methods' => 'GET',
        'permission_callback' => '__return_true',
        'callback' => 'keiba_diag_rest_diagnosis',
    ));
    register_rest_route('keiba-ai/v1', '/diagnosis-status', array(
        'methods' => 'GET',
        'permission_callback' => '__return_true',
        'callback' => 'keiba_diag_rest_status',
    ));
    register_rest_route('keiba-ai/v1', '/diagnosis-event', array(
        'methods' => 'POST',
        'permission_callback' => '__return_true',
        'callback' => 'keiba_diag_rest_event',
    ));
});

function keiba_diag_is_unlocked()
{
    return function_exists('hrc_is_unlocked') && hrc_is_unlocked();
}

function keiba_diag_rest_status()
{
    $now_hhmm = (new DateTimeImmutable('now', new DateTimeZone('Asia/Tokyo')))->format('H:i');
    $response = new WP_REST_Response(keiba_diag_status(keiba_diag_load_races(), $now_hhmm));
    $response->header('Cache-Control', 'no-store');
    return $response;
}

function keiba_diag_rest_diagnosis(WP_REST_Request $request)
{
    $answers = keiba_diag_sanitize_answers(array(
        's' => $request->get_param('s'),
        'p' => $request->get_param('p'),
        'm' => $request->get_param('m'),
        'c' => $request->get_param('c'),
    ));
    if ($answers === null) {
        return new WP_Error('keiba_diag_bad_answers', '回答の形式が正しくありません。', array('status' => 400));
    }

    $loaded = keiba_diag_load_races();
    $unlocked = keiba_diag_is_unlocked();
    $response = array(
        'status' => 'ok',
        'unlocked' => $unlocked,
        'type' => keiba_diag_type_label($answers),
        'relaxed' => false,
        'items' => array(),
        'disclaimer' => KEIBA_DIAG_DISCLAIMER,
    );

    // 診断できるレースが無いときも「該当なし」にはせず、回答に応じた代替診断（競馬スタイル）を返す。
    $to_fallback = function ($reason) use (&$response, $answers) {
        $response['status'] = 'fallback';
        $response['reason'] = $reason; // preparing=データ準備中 / none=条件に合うレースなし
        $response['fallback'] = keiba_diag_fallback($answers);
        return $response;
    };

    if (!$loaded['prepared']) {
        // 本日の開催があるのにデータが無いときだけ「準備中」。開催そのものが無い日は「なし」。
        $any_opened = $loaded['groups']['central']['opened'] || $loaded['groups']['local']['opened'];
        return $to_fallback($any_opened ? 'preparing' : 'none');
    }

    $now_hhmm = (new DateTimeImmutable('now', new DateTimeZone('Asia/Tokyo')))->format('H:i');
    $selected = keiba_diag_select($loaded['races'], $answers, $now_hhmm, 3);
    $response['relaxed'] = $selected['relaxed'];
    if (!$selected['items']) {
        return $to_fallback('none');
    }

    foreach ($selected['items'] as $row) {
        $race = $row['race'];
        $item = array(
            'venue' => $race['venue'],
            'raceNumber' => $race['raceNumber'],
            'raceName' => $race['raceName'],
            'startTime' => $race['startTime'],
            'rank' => $row['rank'],
            'reasons' => keiba_diag_reasons($race, $row['best'], $answers),
            'link' => $race['link'],
            'detail' => null,
        );
        if ($unlocked) {
            $item['detail'] = keiba_diag_detail($race);
        }
        $response['items'][] = $item;
    }
    return $response;
}

function keiba_diag_rest_event(WP_REST_Request $request)
{
    $event = (string) $request->get_param('event');
    if (!in_array($event, keiba_diag_events(), true)) {
        return new WP_Error('keiba_diag_bad_event', 'イベントが正しくありません。', array('status' => 400));
    }
    $own_host = (string) wp_parse_url(home_url(), PHP_URL_HOST);
    $source = keiba_diag_classify_source(
        sanitize_text_field((string) $request->get_param('utm')),
        sanitize_text_field((string) $request->get_param('ref')),
        $own_host
    );
    keiba_diag_record_event($event, $source);
    return array('ok' => true);
}

add_shortcode('keiba_race_diagnosis', function () {
    wp_enqueue_style(
        'keiba-ai-digest-diagnosis',
        plugins_url('assets/keiba-diagnosis.css', dirname(__FILE__)),
        array(),
        KEIBA_AI_DIGEST_VERSION
    );
    wp_enqueue_script(
        'keiba-ai-digest-diagnosis',
        plugins_url('assets/keiba-diagnosis.js', dirname(__FILE__)),
        array(),
        KEIBA_AI_DIGEST_VERSION,
        true
    );
    wp_localize_script('keiba-ai-digest-diagnosis', 'KEIBA_DIAG', array(
        'diagnosisUrl' => esc_url_raw(rest_url('keiba-ai/v1/diagnosis')),
        'statusUrl' => esc_url_raw(rest_url('keiba-ai/v1/diagnosis-status')),
        'eventUrl' => esc_url_raw(rest_url('keiba-ai/v1/diagnosis-event')),
        'addFriendUrl' => esc_url_raw((string) get_option('hrc_line_add_friend_url', '')),
        'generatorUrl' => esc_url_raw(home_url('/smartaigenerator/')),
        'disclaimer' => KEIBA_DIAG_DISCLAIMER,
        'ownHost' => (string) wp_parse_url(home_url(), PHP_URL_HOST),
    ));

    return '<div id="keiba-diagnosis" class="keiba-diagnosis" aria-live="polite">'
        . '<noscript>診断の利用にはJavaScriptを有効にしてください。</noscript></div>';
});

/* ------------------------------------------------------------------------- *
 * 管理画面（設定 → Keiba AI Digest）: 診断の効果測定と、LINE配布用リンク
 * ------------------------------------------------------------------------- */

function keiba_ai_digest_render_diagnosis_admin()
{
    global $wpdb;
    $table = keiba_diag_table();
    $since = (new DateTimeImmutable('-30 days', new DateTimeZone('Asia/Tokyo')))->format('Y-m-d');
    $rows = $wpdb->get_results($wpdb->prepare(
        "SELECT event, source, SUM(total) AS total FROM {$table} WHERE day >= %s GROUP BY event, source",
        $since
    ), ARRAY_A);

    $counts = array();
    foreach ((array) $rows as $row) {
        $counts[$row['source']][$row['event']] = (int) $row['total'];
    }

    $sources = keiba_diag_sources();
    $totals = array();
    foreach (keiba_diag_events() as $event) {
        $totals[$event] = 0;
        foreach ($counts as $by_event) {
            $totals[$event] += isset($by_event[$event]) ? $by_event[$event] : 0;
        }
    }

    $page_id = (int) get_option('keiba_ai_digest_diagnosis_page_id', 0);
    $page_url = $page_id ? get_permalink($page_id) : '';
    $secret = (string) get_option('hrc_unlock_secret_key', '');
    $friend_url = (string) get_option('hrc_line_add_friend_url', '');

    echo '<h2>' . esc_html(KEIBA_DIAG_TITLE) . '（過去30日）</h2>';
    if ($page_url) {
        echo '<p>診断ページ: <a href="' . esc_url($page_url) . '" target="_blank" rel="noopener">' . esc_html($page_url) . '</a></p>';
    }
    if ($friend_url === '') {
        echo '<p><strong>友だち追加URLが未設定です。</strong>ジェネレーター側の設定（友だち追加URL）を入れると、診断の詳細画面にLINEへの案内が出ます。</p>';
    }
    if ($page_url && $secret !== '') {
        echo '<p>LINEの自動応答・リッチメニューに入れる診断ページ用リンク（開くと詳細が解放されます）:<br><code>'
            . esc_html(add_query_arg('hrc_key', $secret, $page_url)) . '</code></p>';
    }

    echo '<table class="widefat striped" style="max-width:860px"><thead><tr>'
        . '<th>流入元</th><th>アクセス</th><th>診断開始</th><th>診断完了</th><th>完了率</th><th>詳細を見る</th><th>LINEボタン</th><th>LINEクリック率</th>'
        . '<th>通常結果の表示</th><th>代替結果の表示</th><th>LINE（通常）</th><th>LINE（代替）</th><th>再診断</th>'
        . '</tr></thead><tbody>';
    $print_row = function ($label, $by_event) {
        $view = isset($by_event['view']) ? $by_event['view'] : 0;
        $start = isset($by_event['start']) ? $by_event['start'] : 0;
        $complete = isset($by_event['complete']) ? $by_event['complete'] : 0;
        $detail = isset($by_event['detail_click']) ? $by_event['detail_click'] : 0;
        $line = isset($by_event['line_click']) ? $by_event['line_click'] : 0;
        echo '<tr><td>' . esc_html($label) . '</td><td>' . (int) $view . '</td><td>' . (int) $start . '</td><td>' . (int) $complete . '</td>'
            . '<td>' . ($start > 0 ? round($complete / $start * 100, 1) . '%' : '-') . '</td>'
            . '<td>' . (int) $detail . '</td><td>' . (int) $line . '</td>'
            . '<td>' . ($complete > 0 ? round($line / $complete * 100, 1) . '%' : '-') . '</td>';
        foreach (array('result_normal', 'result_fallback', 'line_click_normal', 'line_click_fallback', 'recheck') as $extra) {
            echo '<td>' . (int) (isset($by_event[$extra]) ? $by_event[$extra] : 0) . '</td>';
        }
        echo '</tr>';
    };
    foreach ($sources as $key => $label) {
        if (isset($counts[$key])) {
            $print_row($label, $counts[$key]);
        }
    }
    $print_row('合計', $totals);
    echo '</tbody></table>';
    echo '<p class="description">「LINEボタン」は友だち追加ボタンのクリック数で、実際の友だち追加数とは別の数値です。'
        . 'GA4には diagnosis_view / diagnosis_start / diagnosis_complete / diagnosis_detail_click / diagnosis_line_click に加えて、'
        . '通常の診断結果と代替診断結果を区別する diagnosis_result_normal / diagnosis_result_fallback（表示数）、'
        . 'diagnosis_line_click_normal / diagnosis_line_click_fallback（LINEボタンのクリック数）、diagnosis_recheck（再診断）を送信しています。</p>';
}
