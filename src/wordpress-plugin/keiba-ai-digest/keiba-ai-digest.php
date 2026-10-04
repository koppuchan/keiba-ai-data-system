<?php
/**
 * Plugin Name: Keiba AI Digest
 * Description: KeibaDataCollector（JRAVAN+競馬最強の法則WEB 全自動AI競馬データシステム）から送られる
 *              AI指数TOP5・本日の傾向・狙い馬/穴馬/危険な人気馬を、開催場・日単位のカスタム投稿タイプ
 *              「keiba_digest」として受け取り、表示する。既存の Keiba Race Sync（race投稿）とは別。
 * Version: 0.3.0
 */

if (!defined('ABSPATH')) {
    exit;
}

define('KEIBA_AI_DIGEST_VERSION', '0.3.0');
define('KEIBA_AI_DIGEST_JSON_META_KEYS', array(
    'ai_index_top5',
    'trend_morning',
    'trend_live',
    'trend_final',
    'picks',
));

/**
 * カスタム投稿タイプ「keiba_digest」を登録。1開催場・1日につき1投稿
 * （digest_keyで一意。WordPressClient.UpsertDigestAsyncがこれを検索キーに使う）。
 */
add_action('init', function () {
    register_post_type('keiba_digest', array(
        'label' => 'AI指数ダイジェスト',
        'labels' => array(
            'name' => 'AI指数ダイジェスト',
            'singular_name' => 'AI指数ダイジェスト',
        ),
        'public' => true,
        'show_in_rest' => true,
        'rest_base' => 'keiba_digest',
        'supports' => array('title', 'custom-fields'),
        'has_archive' => true,
        'rewrite' => array('slug' => 'ai-digest'),
        'menu_icon' => 'dashicons-chart-line',
    ));
});

/**
 * KeibaDataCollectorが送るメタキーをREST経由で読み書き可能にする。
 * ai_index_top5 / trend_* / picks はcamelCaseキーのJSON文字列として保存する取り決め
 * （WordPressClient.cs側もこの形式で送信する。keiba-race-syncのrace_card等と同じ方針）。
 */
add_action('init', function () {
    foreach (KEIBA_AI_DIGEST_JSON_META_KEYS as $meta_key) {
        register_post_meta('keiba_digest', $meta_key, array(
            'type' => 'string',
            'single' => true,
            'show_in_rest' => true,
            'sanitize_callback' => 'keiba_ai_digest_sanitize_json_meta',
            'auth_callback' => function () {
                return current_user_can('edit_posts');
            },
        ));
    }

    register_post_meta('keiba_digest', 'digest_key', array(
        'type' => 'string',
        'single' => true,
        'show_in_rest' => true,
        'sanitize_callback' => 'sanitize_text_field',
        'auth_callback' => function () {
            return current_user_can('edit_posts');
        },
    ));

    register_post_meta('keiba_digest', 'race_date', array(
        'type' => 'string',
        'single' => true,
        'show_in_rest' => true,
        'sanitize_callback' => 'sanitize_text_field',
        'auth_callback' => function () {
            return current_user_can('edit_posts');
        },
    ));

    register_post_meta('keiba_digest', 'track_code', array(
        'type' => 'string',
        'single' => true,
        'show_in_rest' => true,
        'sanitize_callback' => 'sanitize_text_field',
        'auth_callback' => function () {
            return current_user_can('edit_posts');
        },
    ));

    // LicenseGateの判定結果をそのまま持たせておく。万一データが残っていても
    // 表示側でも二重に隠せるようにする（horse-race-custom-builderのhrc_is_race_visibleと同じ考え方）。
    register_post_meta('keiba_digest', 'license_visible', array(
        'type' => 'boolean',
        'single' => true,
        'show_in_rest' => true,
        'default' => false,
        'auth_callback' => function () {
            return current_user_can('edit_posts');
        },
    ));

    register_post_meta('keiba_digest', 'updated_at', array(
        'type' => 'string',
        'single' => true,
        'show_in_rest' => true,
        'sanitize_callback' => 'sanitize_text_field',
        'auth_callback' => function () {
            return current_user_can('edit_posts');
        },
    ));
});

/**
 * WordPressClient.cs の FindDigestPostIdAsync が使う
 * GET /wp-json/wp/v2/keiba_digest?meta_key=digest_key&meta_value=xxxx を有効にする。
 * keiba-race-syncのrest_race_queryフィルタと同じ考え方（総当たり探索を防ぐため digest_key のみ許可）。
 */
add_filter('rest_keiba_digest_query', function ($args, $request) {
    if ($request->get_param('meta_key') === 'digest_key') {
        $meta_value = $request->get_param('meta_value');
        if ($meta_value !== null && $meta_value !== '') {
            $args['meta_query'] = array(
                array(
                    'key' => 'digest_key',
                    'value' => sanitize_text_field($meta_value),
                ),
            );
        }
    }
    return $args;
}, 10, 2);

function keiba_ai_digest_sanitize_json_meta($value)
{
    if (is_array($value) || is_object($value)) {
        return wp_json_encode($value);
    }
    $value = (string) $value;
    json_decode($value);
    return json_last_error() === JSON_ERROR_NONE ? $value : '[]';
}

/**
 * 仕様書§13「管理画面に自動公開ON/OFFと手動再実行を用意」のうち、自動公開ON/OFF部分。
 * WordPressClient.IsAutoPublishEnabledAsyncがこのGETエンドポイントをポーリングする。
 * 「手動再実行」自体は、収集アプリ側のrun-*.batを運用者がVPS上で直接再実行する運用のまま
 * にしている（このプラグインからVPS上のプロセスを起動する経路は意図的に作らない。
 * Webから任意のバッチ実行を引き起こせる経路を増やすとその分だけ攻撃面が増えるため）。
 */
add_action('rest_api_init', function () {
    register_rest_route('keiba-ai/v1', '/settings', array(
        'methods' => 'GET',
        'permission_callback' => '__return_true',
        'callback' => function () {
            return array(
                'version' => KEIBA_AI_DIGEST_VERSION,
                'autoPublishEnabled' => (bool) get_option('keiba_ai_digest_auto_publish_enabled', false),
            );
        },
    ));

    register_rest_route('keiba-ai/v1', '/settings', array(
        'methods' => 'POST',
        'permission_callback' => function () {
            return current_user_can('manage_options');
        },
        'callback' => function (WP_REST_Request $request) {
            $enabled = (bool) $request->get_param('autoPublishEnabled');
            update_option('keiba_ai_digest_auto_publish_enabled', $enabled);
            return array('autoPublishEnabled' => $enabled);
        },
    ));

    // 仕様書§17監視ダッシュボード。KeibaDataCollectorの`dashboard`コマンドがPOSTで送信し、
    // 管理画面（設定 > Keiba AI Digest）で最新状態を表示する。
    // 認証は他のREST書き込み（register_post_metaのauth_callback）と同じ edit_posts に揃えている
    // （収集アプリのApplication Passwordユーザーが管理者権限を持つとは限らないため）。
    register_rest_route('keiba-ai/v1', '/status', array(
        'methods' => 'POST',
        'permission_callback' => function () {
            return current_user_can('edit_posts');
        },
        'callback' => function (WP_REST_Request $request) {
            $body = $request->get_json_params();
            if (!is_array($body)) {
                return new WP_Error('invalid_body', 'JSON body required', array('status' => 400));
            }
            // 値は検証せずそのまま保存する。ここは収集アプリ（認証済み）専用の内部ステータスで、
            // 表示は管理画面のみ・一般公開はしないため。
            update_option('keiba_ai_digest_last_status', $body);
            update_option('keiba_ai_digest_last_status_at', current_time('mysql', true));
            return array('ok' => true);
        },
    ));

    register_rest_route('keiba-ai/v1', '/status', array(
        'methods' => 'GET',
        'permission_callback' => function () {
            return current_user_can('manage_options');
        },
        'callback' => function () {
            return array(
                'status' => get_option('keiba_ai_digest_last_status', null),
                'receivedAt' => get_option('keiba_ai_digest_last_status_at', null),
            );
        },
    ));
});

/**
 * 管理画面: 設定 > Keiba AI Digest。自動公開ON/OFFのチェックボックスのみ持つ。
 *
 * 既定値はOFF（フェイルクローズ）。仕様書§5・LicenseGateと同じ方針で、
 * 「許諾・準備が整うまでは公開しない」を初期状態にしている。プラグイン導入直後に
 * 意図せず自動公開が始まらないようにするため。
 */
add_action('admin_menu', function () {
    add_options_page(
        'Keiba AI Digest',
        'Keiba AI Digest',
        'manage_options',
        'keiba-ai-digest',
        'keiba_ai_digest_render_settings_page'
    );
});

function keiba_ai_digest_render_settings_page()
{
    if (!current_user_can('manage_options')) {
        return;
    }

    if (isset($_POST['keiba_ai_digest_nonce']) && wp_verify_nonce($_POST['keiba_ai_digest_nonce'], 'keiba_ai_digest_settings')) {
        update_option('keiba_ai_digest_auto_publish_enabled', isset($_POST['auto_publish_enabled']));
        echo '<div class="notice notice-success"><p>保存しました。</p></div>';
    }

    $enabled = (bool) get_option('keiba_ai_digest_auto_publish_enabled', false);
    ?>
    <div class="wrap">
        <h1>Keiba AI Digest 設定</h1>
        <p>
            AI指数TOP5・本日の傾向・狙い馬/穴馬/危険な人気馬のWordPress自動公開を制御します。
            OFFの間は、収集アプリ側でLicenseGate・Validatorの判定が通っていてもWordPressへは反映されません
            （収集アプリ側のスコア算出・コンテンツ生成・ローカルDBへの保存自体は止まりません）。
        </p>
        <form method="post">
            <?php wp_nonce_field('keiba_ai_digest_settings', 'keiba_ai_digest_nonce'); ?>
            <table class="form-table">
                <tr>
                    <th scope="row">自動公開</th>
                    <td>
                        <label>
                            <input type="checkbox" name="auto_publish_enabled" value="1" <?php checked($enabled); ?> />
                            AI指数・傾向・狙い目の自動公開を有効にする
                        </label>
                    </td>
                </tr>
            </table>
            <?php submit_button('保存'); ?>
        </form>
        <h2>手動再実行について</h2>
        <p>
            個別レースの再取得・再公開は、収集アプリ（VPS上のKeibaDataCollector）側で
            <code>run-score.bat</code> / <code>run-content.bat</code> を対象日を指定して再実行してください。
            このプラグインからVPS上の処理を起動する経路は用意していません。
        </p>

        <h2>監視ダッシュボード（仕様書§17）</h2>
        <?php keiba_ai_digest_render_status(); ?>
    </div>
    <?php
}

/**
 * KeibaDataCollectorの`dashboard`コマンドが最後にPOSTしてきたステータスを表示する。
 * 一度も送信されていなければその旨を表示するだけで、エラーにはしない
 * （収集アプリを未導入・`dashboard`未実行のままプラグインだけ入れている状態は正常にありうる）。
 */
function keiba_ai_digest_render_status()
{
    $status = get_option('keiba_ai_digest_last_status', null);
    $receivedAt = get_option('keiba_ai_digest_last_status_at', null);

    if (!is_array($status)) {
        echo '<p>まだ収集アプリから監視データを受信していません。VPS上で <code>KeibaDataCollector.exe dashboard</code> を実行してください。</p>';
        return;
    }

    echo '<p>最終受信（UTC）: ' . esc_html($receivedAt) . '</p>';
    echo '<table class="widefat" style="max-width:800px">';
    $rows = array(
        '対象日' => isset($status['raceDate']) ? $status['raceDate'] : '-',
        '当日開催場' => isset($status['venuesWithData']) ? implode(', ', (array) $status['venuesWithData']) : '-',
        '最終データ同期' => isset($status['lastDataSyncUtc']) ? $status['lastDataSyncUtc'] : '-',
        '最終AI計算' => isset($status['lastAiComputeUtc']) ? $status['lastAiComputeUtc'] : '-',
        '最終WordPress更新' => isset($status['lastWordPressPublishUtc']) ? $status['lastWordPressPublishUtc'] : '-',
        '未処理レース数' => isset($status['unprocessedRaceCount']) ? $status['unprocessedRaceCount'] : '-',
        '自動公開' => !empty($status['autoPublishEnabled']) ? 'ON' : 'OFF',
    );
    foreach ($rows as $label => $value) {
        echo '<tr><th style="text-align:left;width:200px">' . esc_html($label) . '</th><td>' . esc_html($value) . '</td></tr>';
    }
    echo '</table>';

    if (!empty($status['errorCountLast24h']) && is_array($status['errorCountLast24h'])) {
        echo '<h3>エラー件数（直近24時間）</h3><ul>';
        foreach ($status['errorCountLast24h'] as $severity => $count) {
            echo '<li>' . esc_html($severity) . ': ' . esc_html($count) . '件</li>';
        }
        echo '</ul>';
    }

    if (!empty($status['publishBlockedReasons']) && is_array($status['publishBlockedReasons'])) {
        echo '<h3>公開停止理由（本日）</h3><ul>';
        foreach ($status['publishBlockedReasons'] as $reason) {
            echo '<li>' . esc_html($reason) . '</li>';
        }
        echo '</ul>';
    }
}

/* ------------------------------------------------------------------------- *
 * フロント表示（仕様書§3 完成イメージ：AI指数TOP5・本日の傾向・狙い馬/穴馬/危険な人気馬）
 *
 * keiba-race-syncと同じ方針: 本文はエディタで書かず the_content フィルタで自動生成する。
 * テーマのheader/footerはそのまま使われる。
 * ------------------------------------------------------------------------- */

add_action('wp_enqueue_scripts', function () {
    if (is_singular('keiba_digest')) {
        wp_enqueue_style(
            'keiba-ai-digest',
            plugins_url('assets/keiba-ai-digest.css', __FILE__),
            array(),
            KEIBA_AI_DIGEST_VERSION
        );
    }
});

add_filter('the_content', function ($content) {
    if (!is_singular('keiba_digest') || !in_the_loop() || !is_main_query()) {
        return $content;
    }
    return $content . keiba_ai_digest_render_digest(get_the_ID());
}, 10);

function keiba_ai_digest_decode_meta($post_id, $meta_key)
{
    $raw = get_post_meta($post_id, $meta_key, true);
    if (empty($raw)) {
        return null;
    }
    $decoded = json_decode($raw, true);
    return json_last_error() === JSON_ERROR_NONE ? $decoded : null;
}

/**
 * 1開催場・1日分のダイジェストを描画する。
 *
 * license_visibleは公開時点（DigestPublisherService.PublishAsync実行時）のLicenseGate判定を
 * そのまま持たせた値で、表示のたびに許諾状態を再確認するものではない
 * （このプラグイン単体ではLicenseGateの状態を直接参照できないため）。許諾が後から取り消された
 * 場合、収集アプリ側は次回以降の更新を止めるが、既に公開済みのこの投稿自体は次の更新まで
 * 残り続ける。horse-race-custom-builderのhrc_is_race_visibleも同様に「その場でのリアルタイム
 * 取り消し」までは行っておらず、同じ運用上の制約として扱う。
 */
function keiba_ai_digest_render_digest($post_id)
{
    $license_visible = (bool) get_post_meta($post_id, 'license_visible', true);
    if (!$license_visible) {
        return '<p class="keiba-ai-digest-hidden">現在、このコンテンツは公開許諾の都合により表示できません。</p>';
    }

    $top5 = keiba_ai_digest_decode_meta($post_id, 'ai_index_top5');
    $trends = array(
        keiba_ai_digest_decode_meta($post_id, 'trend_morning'),
        keiba_ai_digest_decode_meta($post_id, 'trend_live'),
        keiba_ai_digest_decode_meta($post_id, 'trend_final'),
    );
    $picks = keiba_ai_digest_decode_meta($post_id, 'picks');
    $updated_at = get_post_meta($post_id, 'updated_at', true);

    ob_start();
    echo '<div class="keiba-ai-digest">';

    echo '<p class="keiba-ai-digest-disclaimer">'
        . 'AI指数・傾向・狙い目は過去データおよび当日データの統計的な分析結果であり、'
        . 'レースの結果や利益を保証するものではありません。'
        . '</p>';

    if ($updated_at) {
        echo '<p class="keiba-ai-digest-updated">最終更新: ' . esc_html(keiba_ai_digest_format_time($updated_at)) . '</p>';
    }

    keiba_ai_digest_render_top5($top5);
    keiba_ai_digest_render_trends($trends);
    keiba_ai_digest_render_picks($picks);

    echo '</div>';
    return ob_get_clean();
}

/** 仕様書§9 AI指数TOP5。 */
function keiba_ai_digest_render_top5($top5)
{
    if (empty($top5) || !is_array($top5)) {
        return;
    }

    echo '<section class="keiba-ai-digest-section keiba-ai-digest-top5">';
    echo '<h2>本日のAI指数TOP5</h2>';
    echo '<div class="keiba-table-scroll"><table class="keiba-ai-digest-table">';
    echo '<thead><tr><th>順位</th><th>R</th><th>馬番</th><th>馬名</th><th>AI指数</th><th>データ充足率</th></tr></thead><tbody>';
    $rank = 0;
    foreach ($top5 as $row) {
        $rank++;
        echo '<tr>';
        echo '<td>' . esc_html($rank) . '</td>';
        echo '<td>' . esc_html(isset($row['raceNumber']) ? $row['raceNumber'] . 'R' : '-') . '</td>';
        echo '<td>' . esc_html(isset($row['umaban']) ? $row['umaban'] . '番' : '-') . '</td>';
        echo '<td>' . esc_html($row['horseName'] ?? '-') . '</td>';
        echo '<td>' . esc_html(isset($row['aiIndex']) ? number_format((float) $row['aiIndex'], 1) : '-') . '</td>';
        echo '<td>' . esc_html(isset($row['dataCompleteness']) ? round(((float) $row['dataCompleteness']) * 100) . '%' : '-') . '</td>';
        echo '</tr>';
    }
    echo '</tbody></table></div>';
    echo '</section>';
}

/** 日時文字列（UTC/オフセット付きISO）をサイトのタイムゾーンで表示用に整形する。 */
function keiba_ai_digest_format_time($value)
{
    $text = (string) $value;
    // タイムゾーン表記が無い値はUTCとして扱う（収集アプリはUTCで保存している）。
    if (!preg_match('/(Z|[+-]\d{2}:?\d{2})$/', $text)) {
        $text .= 'Z';
    }
    $ts = strtotime($text);
    return $ts ? wp_date('Y/m/d H:i', $ts) : (string) $value;
}

/** 仕様書§10 本日の傾向。朝・開催中・終了後の各段階を、保存されているものだけ並べて表示する。 */
function keiba_ai_digest_render_trends($trends)
{
    $present = array_filter($trends, function ($t) {
        return !empty($t) && is_array($t);
    });
    if (empty($present)) {
        return;
    }

    echo '<section class="keiba-ai-digest-section keiba-ai-digest-trend">';
    echo '<h2>本日の傾向</h2>';
    foreach ($present as $trend) {
        keiba_ai_digest_render_trend_stage($trend);
    }
    echo '</section>';
}

function keiba_ai_digest_render_trend_stage($trend)
{
    $stage_label = array('Morning' => '朝（事前想定）', 'Live' => '開催中（現時点）', 'Final' => '終了後（本日の結果）');
    $stage = isset($trend['stage']) ? $trend['stage'] : '';

    echo '<div class="keiba-ai-digest-trend-stage">';
    echo '<h3>' . esc_html(isset($stage_label[$stage]) ? $stage_label[$stage] : $stage);
    if (!empty($trend['computedAtUtc'])) {
        echo ' <small>（' . esc_html(keiba_ai_digest_format_time($trend['computedAtUtc'])) . '時点）</small>';
    }
    echo '</h3>';

    $weather = isset($trend['weatherTrack']) ? $trend['weatherTrack'] : null;
    // JV-Dataコード表: 2011 天候コード / 2010 馬場状態コード。0は未設定（初期値）のため表示しない。
    // 表に無いコードは推測せず生のコード値をそのまま出す。
    $weather_labels = array('1' => '晴', '2' => '曇', '3' => '雨', '4' => '小雨', '5' => '雪', '6' => '小雪');
    $going_labels = array('1' => '良', '2' => '稍重', '3' => '重', '4' => '不良');
    $parts = array();
    if ($weather) {
        $items = array(
            array('天候', $weather['weatherCode'] ?? '', $weather_labels),
            array('芝', $weather['turfConditionCode'] ?? '', $going_labels),
            array('ダート', $weather['dirtConditionCode'] ?? '', $going_labels),
        );
        foreach ($items as $item) {
            list($name, $code, $labels) = $item;
            if ($code === '' || $code === '0') {
                continue;
            }
            $parts[] = $name . ': ' . (isset($labels[$code]) ? $labels[$code] : 'コード' . $code);
        }
    }
    if ($parts) {
        echo '<p class="keiba-ai-digest-weather">' . esc_html(implode(' / ', $parts)) . '</p>';
    }

    echo '<ul class="keiba-ai-digest-trend-list">';

    $pace = isset($trend['pace']) ? $trend['pace'] : null;
    if ($pace && !empty($pace['hasEnoughSample'])) {
        $favored = !empty($pace['frontRunnerFavored']) ? '先行有利' : '差し有利';
        echo '<li>脚質傾向: <strong>' . esc_html($favored) . '</strong>（サンプル' . esc_html($pace['sampleCount'] ?? 0) . '件）</li>';
    } else {
        echo '<li>脚質傾向: サンプル不足のため判定なし</li>';
    }

    $agari = isset($trend['agari']) ? $trend['agari'] : null;
    if ($agari && !empty($agari['averageAgari3F'])) {
        echo '<li>上がり3F平均: ' . esc_html(number_format((float) $agari['averageAgari3F'], 1)) . '秒（サンプル' . esc_html($agari['sampleCount'] ?? 0) . '件）</li>';
    }

    $passage = isset($trend['passage']) ? $trend['passage'] : null;
    if ($passage && isset($passage['leaderWinRate'])) {
        echo '<li>最終コーナー先頭馬の勝率: ' . esc_html(round(((float) $passage['leaderWinRate']) * 100)) . '%（サンプル' . esc_html($passage['sampleCount'] ?? 0) . 'レース）</li>';
    } else {
        echo '<li>通過順傾向: サンプル不足のため判定なし</li>';
    }

    echo '</ul>';

    $post_position = isset($trend['postPosition']['byWaku']) ? $trend['postPosition']['byWaku'] : null;
    if ($post_position && is_array($post_position)) {
        echo '<div class="keiba-table-scroll"><table class="keiba-ai-digest-table keiba-ai-digest-waku-table">';
        echo '<thead><tr><th>枠</th><th>連対率</th><th>サンプル数</th></tr></thead><tbody>';
        ksort($post_position, SORT_NUMERIC);
        foreach ($post_position as $waku => $stat) {
            echo '<tr>';
            echo '<td>' . esc_html($waku) . '</td>';
            echo '<td>' . esc_html(isset($stat['placeRate']) ? round(((float) $stat['placeRate']) * 100) . '%' : '判定なし') . '</td>';
            echo '<td>' . esc_html($stat['sampleCount'] ?? 0) . '</td>';
            echo '</tr>';
        }
        echo '</tbody></table></div>';
    }

    echo '</div>';
}

/** 仕様書§11 今日の狙い馬・穴馬・危険な人気馬。 */
function keiba_ai_digest_render_picks($picks)
{
    if (empty($picks) || !is_array($picks)) {
        return;
    }

    $groups = array('Nerai' => array(), 'Ana' => array(), 'Kiken' => array());
    foreach ($picks as $pick) {
        $category = isset($pick['category']) ? $pick['category'] : '';
        if (isset($groups[$category])) {
            $groups[$category][] = $pick;
        }
    }

    $section_labels = array('Nerai' => '今日の狙い馬', 'Ana' => '今日の穴馬', 'Kiken' => '危険な人気馬');

    foreach ($section_labels as $category => $label) {
        if (empty($groups[$category])) {
            continue;
        }
        echo '<section class="keiba-ai-digest-section keiba-ai-digest-picks keiba-ai-digest-picks-' . esc_attr(strtolower($category)) . '">';
        echo '<h2>' . esc_html($label) . '</h2><ul>';
        foreach ($groups[$category] as $pick) {
            echo '<li>' . esc_html($pick['text'] ?? '') . '</li>';
        }
        echo '</ul></section>';
    }
}
